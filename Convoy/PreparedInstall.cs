using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Convoy
{
    // These files are the output of verification, not a verification receipt for a mutable cache path.
    internal sealed class PreparedInstall : IDisposable
    {
        private const int MaxFiles = 4096;
        private const int LinuxFdReserve = 128;
        private const string ScratchPrefix = "install-";
        private const int AtCurrentDir = -100, AtFollowLink = 0x400;
        private const int OpenAnonymous = 0x410000 | 0x80000 | 2; // O_TMPFILE | O_CLOEXEC | O_RDWR (Linux x64)
        private const int RenameInfo = 3, DispositionInfo = 4;
        private readonly bool _windows;
        private readonly Dictionary<string, Slot> _paths;
        private readonly ILookup<string, Slot> _objects;

        private sealed class Slot
        {
            internal readonly SafeHandle Handle;
            internal bool Verified;
            internal Slot(SafeHandle handle) => Handle = handle;
        }

        private sealed class LinuxHandle : SafeHandleMinusOneIsInvalid
        {
            internal LinuxHandle(int fd) : base(true) => SetHandle(new IntPtr(fd));
            protected override bool ReleaseHandle() => CloseLinux(handle.ToInt32()) == 0;
        }

        private PreparedInstall(bool windows, Dictionary<string, Slot> paths, DeliveryAsset[] writes)
        {
            _windows = windows;
            _paths = paths;
            _objects = writes.ToLookup(asset => asset.Sha256, asset => paths[asset.Path], StringComparer.Ordinal);
        }

        internal static PreparedInstall? TryCreate(string directory, DeliveryAsset[] writes, CancellationToken cancel)
        {
            if (writes.Length == 0 || writes.Length > MaxFiles) return null;
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (windows)
            {
                // Wine share modes do not exclude a native host writer. Keep hash-on-copy there.
                var ntdll = GetModuleHandle("ntdll.dll");
                if (ntdll == IntPtr.Zero || GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero) return null;
            }
            else if (!CanPrepareLinux(directory, writes.Length)) return null;

            var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
            var transferred = false;
            try
            {
                foreach (var asset in writes)
                {
                    cancel.ThrowIfCancellationRequested();
                    var handle = windows ? OpenWindows(directory) : OpenLinux(directory);
                    if (handle == null) return null;
                    try
                    {
                        TransferSpool.RequirePrivateFile(handle, anonymous: !windows);
                        slots.Add(asset.Path, new Slot(handle));
                    }
                    catch { DiscardHandle(handle, windows); throw; }
                }
                var prepared = new PreparedInstall(windows, slots, writes);
                transferred = true;
                return prepared;
            }
            finally
            {
                if (!transferred)
                    foreach (var slot in slots.Values) DiscardHandle(slot.Handle, windows);
            }
        }

        internal bool Verify(DeliveryAsset asset, Stream source, CancellationToken cancel)
        {
            if (!_objects.Contains(asset.Sha256)) return FileDelivery.VerifyBytes(asset, source, cancel);
            var slots = _objects[asset.Sha256];
            foreach (var slot in slots)
            {
                cancel.ThrowIfCancellationRequested();
                slot.Verified = false;
                Reset(slot.Handle);
            }
            if (!FileDelivery.VerifyBytes(asset, source, cancel, (buffer, count) =>
            {
                // Native anonymous descriptors are not managed FileStreams on Mono.
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var data = pinned.AddrOfPinnedObject();
                    foreach (var slot in slots)
                    {
                        cancel.ThrowIfCancellationRequested();
                        Write(slot.Handle, data, count);
                    }
                }
                finally { pinned.Free(); }
            })) return false;
            foreach (var slot in slots) slot.Verified = true;
            return true;
        }

        private void Reset(SafeHandle handle)
        {
            if (_windows)
            {
                if (!SetFilePointerEx(handle, 0, out _, 0) || !SetEndOfFile(handle)) throw IoError("reset prepared file");
            }
            else if (TruncateLinux(Fd(handle), 0) != 0 || SeekLinux(Fd(handle), 0, 0) != 0)
                throw IoError("reset prepared file");
        }

        private void Write(SafeHandle handle, IntPtr data, int remaining)
        {
            while (remaining > 0)
            {
                int written;
                if (_windows)
                {
                    if (!WriteFile(handle, data, (uint)remaining, out var count, IntPtr.Zero)) throw IoError("write prepared file");
                    written = (int)count;
                }
                else
                {
                    written = WriteLinux(Fd(handle), data, (UIntPtr)(uint)remaining).ToInt32();
                    if (written < 0 && Marshal.GetLastWin32Error() == 4) continue; // EINTR
                    if (written < 0) throw IoError("write prepared file");
                }
                if (written == 0 || written > remaining) throw new IOException("Short write to Convoy prepared file");
                data = IntPtr.Add(data, written);
                remaining -= written;
            }
        }

        internal void Place(string path, string target)
        {
            var slot = _paths[path];
            if (!slot.Verified) throw new IOException("Convoy install file is not verified: " + path);
            TransferSpool.RequirePrivateFile(slot.Handle, anonymous: !_windows);
            // No overwrite: a new target that appeared since the fence is not ours to replace.
            if (_windows) RenameWindows(slot.Handle, target);
            else if (LinkAt(AtCurrentDir, FdPath(slot.Handle), AtCurrentDir, target, AtFollowLink) != 0)
                throw IoError("publish verified file " + path);
            slot.Handle.Dispose();
        }

        public void Dispose()
        {
            foreach (var slot in _paths.Values) DiscardHandle(slot.Handle, _windows);
        }

        private static void DiscardHandle(SafeHandle handle, bool windows)
        {
            if (windows && !handle.IsClosed)
                SetFileInformationByHandle(handle, DispositionInfo, new byte[] { 1 }, 1);
            handle.Dispose();
        }

        internal static bool ScratchName(string name) => name.StartsWith(ScratchPrefix, StringComparison.Ordinal)
            && name.EndsWith(".tmp", StringComparison.Ordinal) && name.Length == ScratchPrefix.Length + 36
            && Guid.TryParseExact(name.Substring(ScratchPrefix.Length, 32), "N", out _);

        private static string ScratchPath(string directory) => Path.Combine(directory, ScratchPrefix + Guid.NewGuid().ToString("N") + ".tmp");

        private static bool CanPrepareLinux(string directory, int count)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || !Directory.Exists("/proc/self/fd")) return false;
            var limit = SysConf(4); // _SC_OPEN_MAX
            if (limit < count + LinuxFdReserve + Directory.EnumerateFileSystemEntries("/proc/self/fd").Count()) return false;
            using var handle = OpenLinux(directory);
            if (handle == null) return false;
            var probe = ScratchPath(directory);
            if (LinkAt(AtCurrentDir, FdPath(handle), AtCurrentDir, probe, AtFollowLink) != 0) return false;
            File.Delete(probe);
            return true;
        }

        private static int Fd(SafeHandle handle) => handle.DangerousGetHandle().ToInt32();
        private static string FdPath(SafeHandle handle) => "/proc/self/fd/" + Fd(handle).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static SafeHandle? OpenLinux(string directory)
        {
            var fd = Open(directory, OpenAnonymous, 438); // Ordinary output permissions, subject to umask.
            if (fd >= 0) return new LinuxHandle(fd);
            var error = Marshal.GetLastWin32Error();
            if (error == 21 || error == 22 || error == 23 || error == 24 || error == 95) return null; // Unsupported or descriptor limit.
            throw IoError("prepare install file", error);
        }

        private static SafeFileHandle? OpenWindows(string directory)
        {
            // GENERIC_READ | GENERIC_WRITE | DELETE; no sharing; CREATE_NEW; OPEN_REPARSE_POINT.
            var handle = CreateFile(ScratchPath(directory), 0xc0010000, 0, IntPtr.Zero, 1, 0x00200080, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 1 || error == 4 || error == 5 || error == 50) return null;
            throw IoError("prepare install file", error);
        }

        private static void RenameWindows(SafeHandle source, string target)
        {
            var name = Encoding.Unicode.GetBytes(target);
            // FILE_RENAME_INFO: false ReplaceIfExists, null RootDirectory, byte count, UTF-16 path.
            var lengthOffset = IntPtr.Size * 2;
            var info = new byte[lengthOffset + sizeof(int) + name.Length + sizeof(char)];
            Buffer.BlockCopy(BitConverter.GetBytes(name.Length), 0, info, lengthOffset, sizeof(int));
            Buffer.BlockCopy(name, 0, info, lengthOffset + sizeof(int), name.Length);
            if (!SetFileInformationByHandle(source, RenameInfo, info, (uint)info.Length))
                throw IoError("publish verified file " + target);
        }

        private static IOException IoError(string action, int? error = null)
        {
            var cause = new Win32Exception(error ?? Marshal.GetLastWin32Error());
            return new IOException("Could not " + action + ": " + cause.Message, cause);
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string directory, int flags, uint mode);
        [DllImport("libc", EntryPoint = "close")]
        private static extern int CloseLinux(int fd);
        [DllImport("libc", EntryPoint = "sysconf")]
        private static extern long SysConf(int name);
        [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
        private static extern int LinkAt(int oldDir, string oldName, int newDir, string newName, int flags);
        [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
        private static extern int TruncateLinux(int fd, long length);
        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        private static extern long SeekLinux(int fd, long offset, int origin);
        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern IntPtr WriteLinux(int fd, IntPtr data, UIntPtr length);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeHandle file, int kind, byte[] info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeHandle file, long offset, out long position, uint origin);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEndOfFile(SafeHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeHandle file, IntPtr data, uint length, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
