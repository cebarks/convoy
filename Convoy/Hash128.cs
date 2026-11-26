using System;
using System.IO;
using System.IO.Hashing;
using System.Threading;

namespace Convoy
{
    internal static class Hash128
    {
        internal static string Canonical(XxHash128 hash) =>
            BitConverter.ToString(hash.GetCurrentHash()).Replace("-", "").ToLowerInvariant();

        internal static string Compute(Stream source, CancellationToken cancel)
        {
            var hash = new XxHash128();
            var buffer = new byte[64 * 1024];
            int count;
            cancel.ThrowIfCancellationRequested();
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancel.ThrowIfCancellationRequested();
                hash.Append(buffer.AsSpan(0, count));
            }
            cancel.ThrowIfCancellationRequested();
            return Canonical(hash);
        }
    }
}
