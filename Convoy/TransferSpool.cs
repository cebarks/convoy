using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Convoy
{
    internal sealed class TransferSpool : IDisposable
    {
        internal readonly struct PartialState
        {
            internal readonly string Path;
            internal readonly long ExpectedLength, Length;
            internal readonly string? ETag;
            internal PartialState(string path, long expected, long length, string? etag)
            { Path = path; ExpectedLength = expected; Length = length; ETag = etag; }
        }

        private const string Root = "BepInEx/config/Convoy/transfer-v2";
        internal const long DefaultMaxBytes = 68719476736L;
        private static readonly HashSet<string> Held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SyncFiles _paths;
        private readonly string _root, _originKey;
        private readonly long _maxBytes;
        private readonly FileStream _lock;
        internal Uri Origin { get; }

        internal static TransferSpool Acquire(string gameRoot, Uri origin, long maxBytes) => new TransferSpool(gameRoot, origin, maxBytes);

        private TransferSpool(string gameRoot, Uri origin, long maxBytes)
        {
            _root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var parent = new DirectoryInfo(_root); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked game root or ancestor");
            _paths = new SyncFiles(_root, Array.Empty<ModState>());
            Origin = origin;
            _maxBytes = maxBytes;
            using var hash = SHA256.Create();
            _originKey = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(origin.AbsoluteUri))).Replace("-", "").ToLowerInvariant();
            lock (Held)
                if (!Held.Add(_root)) throw new IOException("Another Convoy operation holds this game root; retry when it finishes");
            FileStream? handle = null;
            try
            {
                Directory.CreateDirectory(_paths.Resolve(Root));
                handle = new FileStream(_paths.Resolve(Root + "/root.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                RequirePrivateFile(handle);
                handle.Lock(0, 1);
                _lock = handle;
            }
            catch (Exception ex)
            {
                handle?.Dispose();
                lock (Held) Held.Remove(_root);
                throw new IOException("Cannot lock Convoy game root; check permissions or retry when the other operation finishes", ex);
            }
        }

        public void Dispose()
        {
            try { _lock.Dispose(); }
            finally { lock (Held) Held.Remove(_root); }
        }

        internal string Epoch()
        {
            _paths.Resolve(Root + "/root.lock");
            _lock.Position = 0;
            if (_lock.Length == 0) return "";
            var bytes = new byte[32];
            if (_lock.Length != bytes.Length || _lock.Read(bytes, 0, bytes.Length) != bytes.Length)
                throw new IOException("Invalid Convoy lock epoch");
            return Encoding.ASCII.GetString(bytes);
        }

        internal void AdvanceEpoch()
        {
            _paths.Resolve(Root + "/root.lock");
            RequirePrivateFile(_lock);
            var bytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
            _lock.Position = 0;
            _lock.Write(bytes, 0, bytes.Length);
            _lock.Flush();
        }

        // Linux statx has a fixed UAPI layout independent of libc stat/time_t ABI.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(16)] public uint Links;
            [FieldOffset(28)] public ushort Mode;
        }
        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int fd, string name, int flags, uint mask, out LinuxStatx info);

        // BY_HANDLE_FILE_INFORMATION uses DWORDs, including each half of FILETIME.
        [StructLayout(LayoutKind.Explicit, Size = 52)]
        private struct WindowsFileInfo
        {
            [FieldOffset(0)] public uint Attributes;
            [FieldOffset(40)] public uint Links;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);

        private static void RequirePrivateFile(FileStream stream)
        {
            var handle = stream.SafeFileHandle;
            bool safe;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                safe = GetFileType(handle) == 1 && GetFileInformationByHandle(handle, out var info)
                    && info.Links == 1 && (info.Attributes & 0x410) == 0;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // AT_EMPTY_PATH reads the very descriptor that will be mutated.
                const uint fields = 0x5; // STATX_TYPE | STATX_NLINK
                safe = statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, fields, out var info) == 0
                    && (info.Mask & fields) == fields && (info.Mode & 0xf000) == 0x8000 && info.Links == 1;
                GC.KeepAlive(handle);
            }
            else throw new PlatformNotSupportedException("Convoy spool requires Windows or Linux file identity checks");
            if (!safe) throw new IOException("Convoy mutable spool entry is not a singly linked regular file");
        }

        internal FileStream OpenDataForWrite(DeliveryAsset asset, bool append, long length)
        {
            var stream = new FileStream(DataPath(asset), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                RequirePrivateFile(stream);
                if (append)
                {
                    if (stream.Length != length) throw new IOException("Convoy checkpoint changed before append");
                    stream.Position = length;
                }
                else stream.SetLength(0);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        private string Key(DeliveryAsset asset)
        {
            if (asset.Length < 0 || !DeliveryManifest.Hex(asset.Sha256, 64)) throw new InvalidDataException("Invalid staged asset");
            return _originKey + "-" + asset.Sha256 + "-" + asset.Length.ToString(CultureInfo.InvariantCulture);
        }
        private string PathFor(DeliveryAsset asset, string suffix) => CheckedEntry(Key(asset) + suffix);

        private string CheckedEntry(string name)
        {
            var path = Path.Combine(_paths.Resolve(Root), name);
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Unexpected non-file at Convoy staging path: " + name);
            }
            catch (FileNotFoundException) { }
            return path;
        }

        internal string DataPath(DeliveryAsset asset) => PathFor(asset, ".data");

        internal void CopyVerified(DeliveryAsset asset, Stream output)
        {
            using var source = new FileStream(DataPath(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
            RequirePrivateFile(source);
            FileDelivery.CopyVerified(asset, source, output);
        }

        internal PartialState OpenPartial(DeliveryAsset asset)
        {
            var data = DataPath(asset);
            File.Delete(PathFor(asset, ".tmp"));
            var meta = PathFor(asset, ".meta");
            if (File.Exists(data) && File.Exists(meta) && new FileInfo(meta).Length <= 100)
            {
                var parts = File.ReadAllText(meta).Split('\n');
                var length = new FileInfo(data).Length;
                if (parts.Length == 2 && parts[0] == DeliveryManifest.Tag(asset.Sha256)
                    && parts[1] == length.ToString(CultureInfo.InvariantCulture) && length <= asset.Length)
                    return new PartialState(data, asset.Length, length, parts[0]);
            }
            Drop(asset);
            return new PartialState(data, asset.Length, 0, null);
        }

        internal void SaveCheckpoint(DeliveryAsset asset, string etag, long persistedLength)
        {
            if (etag != DeliveryManifest.Tag(asset.Sha256) || persistedLength < 0 || persistedLength > asset.Length
                || new FileInfo(PathFor(asset, ".data")).Length != persistedLength) throw new IOException("Invalid Convoy checkpoint");
            var tmp = PathFor(asset, ".tmp");
            var meta = PathFor(asset, ".meta");
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
                writer.Write(etag + "\n" + persistedLength.ToString(CultureInfo.InvariantCulture));
            if (File.Exists(meta)) File.Replace(tmp, meta, null);
            else File.Move(tmp, meta);
        }

        internal void MarkVerified(DeliveryAsset asset) => SaveCheckpoint(asset, DeliveryManifest.Tag(asset.Sha256), asset.Length);
        internal void Drop(DeliveryAsset asset)
        {
            File.Delete(PathFor(asset, ".meta"));
            File.Delete(PathFor(asset, ".data"));
            File.Delete(PathFor(asset, ".tmp"));
        }

        private static bool TransferFile(string name)
        {
            var ext = Path.GetExtension(name);
            if (ext != ".meta" && ext != ".data" && ext != ".tmp") return false;
            var parts = Path.GetFileNameWithoutExtension(name).Split('-');
            return parts.Length == 3 && DeliveryManifest.Hex(parts[0], 64) && DeliveryManifest.Hex(parts[1], 64)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        internal void Reserve(DeliveryAsset[] assets, long applyBytes)
        {
            var planned = assets.Aggregate(0L, (sum, a) => checked(sum + a.Length + 256));
            if (planned > _maxBytes) throw new IOException("Convoy transfer exceeds the configured spool cap; increase the cap or select fewer mods");
            var keep = new HashSet<string>(assets.Select(Key), StringComparer.Ordinal);
            long cached = 0;
            foreach (var group in Directory.EnumerateFiles(_paths.Resolve(Root)).Select(Path.GetFileName)
                         .Where(TransferFile).GroupBy(n => Path.GetFileNameWithoutExtension(n)))
            {
                if (keep.Contains(group.Key)) continue;
                var paths = group.Select(CheckedEntry).ToArray();
                var bytes = paths.Sum(p => new FileInfo(p).Length);
                if (bytes > _maxBytes - planned - cached || paths.All(p => File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-7)))
                    foreach (var path in paths) File.Delete(path);
                else cached += bytes;
            }
            var missing = assets.Aggregate(0L, (sum, a) => checked(sum + a.Length - OpenPartial(a).Length + 8192));
            RequireFree(checked(missing + applyBytes));
        }

        internal void RequireFree(long bytes, Func<string, long>? available = null)
        {
            if ((available ?? FreeSpace)(_paths.Resolve(Root)) < bytes) throw new IOException("Not enough free space for Convoy staging and rollback; free space and retry");
        }

        internal void CheckVolumes(IEnumerable<string> targets, Func<string, string>? identity = null)
        {
            identity ??= VolumeIdentity;
            var volume = identity(_paths.Resolve(Root));
            if (targets.Any(target => identity(target) != volume))
                throw new IOException("Convoy staging and live paths must be on the same volume");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathName(string path, StringBuilder volume, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);

        private static string VolumeIdentity(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Volume(path).Name;
            while (!Directory.Exists(path) && !File.Exists(path))
                path = Path.GetDirectoryName(path) ?? throw new IOException("Cannot resolve Convoy destination volume");
            var volume = new StringBuilder(32768);
            if (!GetVolumePathName(path, volume, (uint)volume.Capacity)) throw new IOException("Cannot resolve Convoy destination volume");
            return volume.ToString().ToUpperInvariant();
        }

        private static long FreeSpace(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Volume(path).AvailableFreeSpace;
            if (!GetDiskFreeSpaceEx(path, out var available, out _, out _)) throw new IOException("Cannot read Convoy volume free space");
            return (long)Math.Min((ulong)long.MaxValue, available);
        }

        private static DriveInfo Volume(string path) =>
            DriveInfo.GetDrives().Where(d => (path + Path.DirectorySeparatorChar).StartsWith(
                    d.Name.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(path));

    }
}
