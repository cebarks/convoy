using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json.Linq;
using System.IO.Hashing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Convoy
{
    internal sealed class FileDelivery
    {
        private const string CatalogPath = "/quma/convoy/v2/catalog";
        private const int BufferSize = 64 * 1024;
        private const int MaxManifestChars = 32 * 1024 * 1024;

        internal static Uri Origin(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")
                || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("Convoy requires a configured SPT HTTP(S) origin");
            return new Uri(uri.GetLeftPart(UriPartial.Authority));
        }

        internal static HttpWebRequest Request(Uri origin, string path)
        {
            var request = WebRequest.CreateHttp(new Uri(origin, path));
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Proxy = null;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.BypassCache);
            request.Timeout = 15_000;
            request.ReadWriteTimeout = 30_000;
            return request;
        }

        private static HttpWebResponse Open(HttpWebRequest request)
        {
            try { return (HttpWebResponse)request.GetResponse(); }
            catch (WebException ex) when (ex.Response is HttpWebResponse response) { return response; }
        }

        private static void Require(HttpWebResponse response, HttpStatusCode status)
        {
            if (response.StatusCode != status || !string.IsNullOrEmpty(response.Headers[HttpResponseHeader.ContentEncoding]))
                throw new IOException($"Unexpected Convoy delivery response: HTTP {(int)response.StatusCode} (expected {(int)status}) or content encoding");
        }

        internal DeliveryManifest Fetch(Uri origin, SyncProgress? progress, CancellationToken cancel) =>
            Manifest(origin, null, progress, cancel)!.Catalog!;

        internal Func<long> MonotonicMilliseconds { get; set; } =
            () => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));

        internal ManifestBudget NewManifestBudget() => new ManifestBudget(MonotonicMilliseconds);

        internal sealed class ManifestBudget
        {
            private readonly Func<long> _clock;
            private readonly object _gate = new object();
            private long? _start;
            private int _retries;

            internal ManifestBudget(Func<long> clock) => _clock = clock;

            internal void StartPreparation()
            {
                lock (_gate) _start ??= _clock();
            }

            internal double PreparationRemaining()
            {
                lock (_gate)
                {
                    var remaining = 180 - (_clock() - _start!.Value) / 1000.0;
                    if (remaining <= 0) throw new DeliveryFailure("server still preparing; retry");
                    return remaining;
                }
            }

            internal bool TryReserveRetry(out int backoff)
            {
                lock (_gate)
                {
                    if (_retries == 3) { backoff = 0; return false; }
                    backoff = 1 << _retries++;
                    return true;
                }
            }
        }

        internal bool IsCurrent(Uri origin, string generation, SyncProgress? progress, CancellationToken cancel,
            ManifestBudget? budget = null, int? initialDelay = null) =>
            DeliveryManifest.Hex(generation, 64) && Manifest(origin, DeliveryManifest.Tag(generation), progress, cancel, budget, initialDelay) != null;

        private sealed class ManifestResponse
        {
            internal readonly DeliveryManifest? Catalog;
            internal ManifestResponse(DeliveryManifest? catalog) => Catalog = catalog;
        }

        private ManifestResponse? Manifest(Uri origin, string? tag, SyncProgress? progress, CancellationToken cancel,
            ManifestBudget? budget = null, int? initialDelay = null)
        {
            budget ??= NewManifestBudget();
            var polling = initialDelay.HasValue;
            if (polling) WaitForPreparation(budget, initialDelay, progress, cancel);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                // A fresh check may observe recovery even after the shared polling window.
                // Only a request already polling is fenced by the remaining prepare time.
                var deadline = polling ? Math.Min(15, budget.PreparationRemaining()) : 15;
                try { return ManifestOnce(origin, tag, cancel, deadline); }
                catch (DeliveryFailure ex) when (ex.Code == "preparing")
                {
                    polling = true;
                    WaitForPreparation(budget, ex.Delay, progress, cancel);
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested && Transient(ex))
                {
                    var remaining = polling ? budget.PreparationRemaining() : double.PositiveInfinity;
                    if (!budget.TryReserveRetry(out var backoff)) throw;
                    progress?.SetPhase("Retrying");
                    Wait(Math.Min((ex as DeliveryFailure)?.Delay ?? backoff, remaining), cancel);
                }
            }
        }

        private static void WaitForPreparation(ManifestBudget budget, int? delay, SyncProgress? progress, CancellationToken cancel)
        {
            budget.StartPreparation();
            progress?.SetPhase("Preparing on server");
            Wait(Math.Min(Math.Max(1, delay ?? 1), budget.PreparationRemaining()), cancel);
        }

        private static bool Transient(Exception ex) => ex is WebException web &&
            (web.Status == WebExceptionStatus.Timeout || web.Status == WebExceptionStatus.ConnectFailure ||
             web.Status == WebExceptionStatus.ConnectionClosed || web.Status == WebExceptionStatus.ReceiveFailure ||
             web.Status == WebExceptionStatus.SendFailure || web.Status == WebExceptionStatus.KeepAliveFailure)
            || ex is EndOfStreamException || ex is DeliveryFailure failure && failure.Transient;

        private static ManifestResponse? ManifestOnce(Uri origin, string? tag, CancellationToken cancel, double budget)
        {
            var request = Request(origin, CatalogPath);
            if (tag != null) request.Headers[HttpRequestHeader.IfNoneMatch] = tag;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            // The linked abort covers the combined headers/body interval. Leave the idle
            // timeout longer so a deadline is consistently distinguishable from a transport error.
            deadline.CancelAfter(TimeSpan.FromSeconds(budget));
            using var registration = deadline.Token.Register(request.Abort);
            try
            {
                using var response = Open(request);
                if (tag == null && ((int)response.StatusCode == 404 || (int)response.StatusCode == 410 || (int)response.StatusCode == 303))
                    throw new IOException("Convoy v2 is unavailable on this server. Upgrade Quartermaster and install the standalone Convoy client with the game closed: https://github.com/cebarks/convoy#installation");
                CheckStatus(response);
                deadline.Token.ThrowIfCancellationRequested();
                if (tag != null)
                    return response.StatusCode == HttpStatusCode.NotModified && response.Headers[HttpResponseHeader.ETag] == tag
                        && string.IsNullOrEmpty(response.Headers[HttpResponseHeader.ContentEncoding]) ? new ManifestResponse(null) : null;
                Require(response, HttpStatusCode.OK);
                return new ManifestResponse(ReadManifest(origin, response, deadline.Token));
            }
            catch (Exception ex) when (ex is IOException || ex is WebException || ex is OperationCanceledException)
            {
                cancel.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new DeliveryFailure("catalog request timed out", transient: true);
                throw;
            }
        }

        private static DeliveryManifest ReadManifest(Uri origin, HttpWebResponse response, CancellationToken cancel)
        {
            using var reader = new StreamReader(response.GetResponseStream(), new UTF8Encoding(false, true), false);
            var json = new StringBuilder();
            var buffer = new char[8192];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (json.Length + count > MaxManifestChars) throw new InvalidDataException("Convoy catalog exceeds metadata limit");
                json.Append(buffer, 0, count);
            }
            cancel.ThrowIfCancellationRequested();
            return DeliveryManifest.Parse(origin, json.ToString(), response.Headers[HttpResponseHeader.ETag] ?? "");
        }

        internal sealed class DeliveryFailure : IOException
        {
            internal readonly string Code;
            internal readonly int? Delay;
            internal readonly bool Transient;
            internal DeliveryFailure(string code, int? delay = null, bool transient = false)
                : base("Convoy delivery: " + code) { Code = code; Delay = delay; Transient = transient; }
        }

        private static void Wait(double seconds, CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(0, seconds)))) cancel.ThrowIfCancellationRequested();
        }

        private static void CheckStatus(HttpWebResponse response)
        {
            var status = (int)response.StatusCode;
            if (status < 400) return;
            var code = "HTTP " + status;
            if (status == 409 || status == 503)
            {
                using var body = response.GetResponseStream();
                var buffer = new byte[4096];
                var size = 0;
                int count;
                try
                {
                    while (size < buffer.Length && (count = body.Read(buffer, size, buffer.Length - size)) != 0) size += count;
                }
                catch (IOException ex) { throw new EndOfStreamException("Interrupted Convoy error response", ex); }
                if (response.ContentLength > size && size < buffer.Length) throw new EndOfStreamException("Truncated Convoy error response");
                try { code = (string?)JObject.Parse(Encoding.UTF8.GetString(buffer, 0, size))["code"] ?? code; }
                catch (Newtonsoft.Json.JsonException) { }
            }
            var retry = response.Headers[HttpResponseHeader.RetryAfter];
            int? delay = long.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? (int)Math.Min(15, seconds) : (int?)null;
            if (delay == null && DateTimeOffset.TryParseExact(retry, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                delay = (int)Math.Min(15, Math.Max(0, Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds)));
            throw new DeliveryFailure(code, delay, code == "busy" || status == 429 || status == 500 || status == 502 || status == 504);
        }

        // Drop complete objects before reservation so the replacement transfer is included in disk admission.
        // A checkpoint shorter than the object still goes through the usual validator/range path.
        internal static void PrepareForcedRetrieval(DeliveryAsset asset, TransferSpool spool)
        {
            var partial = spool.OpenPartial(asset);
            if (partial.ETag != null && partial.Length == asset.Length) spool.Drop(asset);
        }

        internal string Stage(Uri origin, string generation, DeliveryAsset asset, TransferSpool spool, SyncProgress? progress,
            CancellationToken cancel, ManifestBudget manifestBudget, string connectionGroup, PreparedInstall? prepared)
        {
            if (spool.Origin != origin || !DeliveryManifest.Hex(generation, 64) || !DeliveryManifest.Hex(asset.Sha256, 64))
                throw new InvalidDataException("Invalid Convoy object address");
            var initialCacheBytes = spool.OpenPartial(asset).Length;
            for (var attempt = 0; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try { return StageOnce(origin, generation, asset, spool, progress, cancel, connectionGroup, prepared, ref initialCacheBytes); }
                catch (Exception ex)
                {
                    progress?.ResetAttempt(asset.Sha256);
                    cancel.ThrowIfCancellationRequested();
                    if (ex is DeliveryFailure preparing && preparing.Code == "preparing")
                    {
                        manifestBudget.StartPreparation();
                        if (attempt == 3) throw;
                        if (!IsCurrent(origin, generation, progress, cancel, manifestBudget, preparing.Delay ?? 1))
                            throw new DeliveryFailure("stale_generation");
                        continue;
                    }
                    if (attempt == 3 || !Transient(ex)) throw;
                    progress?.SetPhase("Retrying");
                    Wait((ex as DeliveryFailure)?.Delay ?? (1 << attempt), cancel);
                }
            }
        }

        private static string StageOnce(Uri origin, string generation, DeliveryAsset asset, TransferSpool spool, SyncProgress? progress,
            CancellationToken cancel, string connectionGroup, PreparedInstall? prepared, ref long initialCacheBytes)
        {
            var partial = spool.OpenPartial(asset);
            var reused = partial.Length;
            initialCacheBytes = Math.Min(reused, initialCacheBytes);
            if (partial.ETag != null && reused == asset.Length)
            {
                progress?.SetPhase("Verifying");
                if (VerifyStaged(asset, spool, prepared, cancel))
                {
                    progress?.Accept(asset.Sha256, initialCacheBytes, asset.Length - initialCacheBytes);
                    return partial.Path;
                }
                initialCacheBytes = 0;
                spool.Drop(asset);
                throw new DeliveryFailure("cached object checksum mismatch: " + asset.Path, transient: true);
            }

            var request = Request(origin, $"/quma/convoy/v2/generations/{generation}/objects/{asset.Sha256}");
            request.ConnectionGroupName = connectionGroup;
            request.Timeout = 30_000;
            if (reused > 0)
            {
                request.AddRange(reused);
                request.Headers[HttpRequestHeader.IfRange] = partial.ETag;
            }
            progress?.SetPhase("Downloading");
            using var registration = cancel.Register(request.Abort);
            using var response = Open(request);
            var append = response.StatusCode == HttpStatusCode.PartialContent;
            if ((int)response.StatusCode == 416 && reused > 0)
            {
                initialCacheBytes = 0;
                spool.Drop(asset);
                throw new DeliveryFailure("range no longer satisfiable: " + asset.Path, transient: true);
            }
            CheckStatus(response);
            Require(response, append ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            if (append && reused == 0) throw new InvalidDataException("Unsolicited Convoy object range");
            if (!append) reused = 0;
            var range = append ? FormattableString.Invariant($"bytes {reused}-{asset.Length - 1}/{asset.Length}") : null;
            var tag = DeliveryManifest.Tag(asset.Sha256);
            if (response.Headers[HttpResponseHeader.ETag] != tag
                || response.ContentLength != asset.Length - reused || response.Headers[HttpResponseHeader.ContentRange] != range
                || response.Headers[HttpResponseHeader.TransferEncoding] != null || response.Headers[HttpResponseHeader.AcceptRanges] != "bytes")
                throw new InvalidDataException("Invalid Convoy object metadata: " + asset.Path);

            if (!append) initialCacheBytes = 0;
            progress?.StartObject(asset.Sha256, initialCacheBytes, reused - initialCacheBytes);
            using var stream = response.GetResponseStream();
            long length = reused;
            var checkpoint = false;
            try
            {
                using var file = spool.OpenDataForWrite(asset, append, length);
                checkpoint = true;
                var buffer = new byte[BufferSize];
                while (length < asset.Length)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.BeforeObjectRead?.Invoke(length);
                    int count;
                    try { count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, asset.Length - length)); }
                    catch (IOException ex) { throw new EndOfStreamException("Interrupted Convoy object: " + asset.Path, ex); }
                    if (count == 0) throw new EndOfStreamException("Truncated Convoy object: " + asset.Path);
                    file.Write(buffer, 0, count);
                    progress?.Receive(asset.Sha256, count);
                    length += count;
                }
            }
            finally
            {
                var data = spool.DataPath(asset);
                if (checkpoint && File.Exists(data) && new FileInfo(data).Length == length)
                    spool.SaveCheckpoint(asset, tag, length);
            }
            progress?.SetPhase("Verifying");
            if (!VerifyStaged(asset, spool, prepared, cancel))
            {
                initialCacheBytes = 0;
                spool.Drop(asset);
                throw new DeliveryFailure("object checksum mismatch: " + asset.Path, transient: true);
            }
            spool.MarkVerified(asset);
            progress?.Accept(asset.Sha256, initialCacheBytes, asset.Length - initialCacheBytes);
            return partial.Path;
        }

        private static bool VerifyStaged(DeliveryAsset asset, TransferSpool spool, PreparedInstall? prepared, CancellationToken cancel)
        {
            using var source = spool.OpenDataForRead(asset);
            return prepared != null ? prepared.Verify(asset, source, cancel) : VerifyBytes(asset, source, cancel);
        }

        internal static DeliveryAsset Measure(string path, string file, CancellationToken cancel = default, Action<string>? onChunk = null)
        {
            using var source = File.OpenRead(file);
            return Measure(path, source, cancel, onChunk)!;
        }

        internal static bool VerifyBytes(DeliveryAsset expected, Stream source, CancellationToken cancel, Action<byte[], int>? write = null) =>
            Measure(expected.Path, source, cancel, null, write, expected.Length)?.Same(expected) == true;

        // Compatibility path for filesystems where we cannot hold prepared output private until placement.
        internal static void CopyVerified(DeliveryAsset expected, Stream source, Stream output)
        {
            if (!VerifyBytes(expected, source, default, (buffer, count) => output.Write(buffer, 0, count)))
                throw new IOException("Verified staged object changed before install: " + expected.Path);
        }

        private static DeliveryAsset? Measure(string path, Stream stream, CancellationToken cancel, Action<string>? onChunk,
            Action<byte[], int>? write = null, long maxLength = long.MaxValue)
        {
            var buffer = new byte[BufferSize];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var xxh = new XxHash3();
            long total = 0;
            cancel.ThrowIfCancellationRequested();
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (count > maxLength - total) return null;
                total += count;
                write?.Invoke(buffer, count);
                sha.AppendData(buffer, 0, count);
                xxh.Append(buffer.AsSpan(0, count));
                onChunk?.Invoke(path);
                cancel.ThrowIfCancellationRequested();
            }
            return new DeliveryAsset(path, total, BitConverter.ToString(sha.GetHashAndReset()).Replace("-", "").ToLowerInvariant(), xxh.GetCurrentHashAsUInt64().ToString("x16"));
        }
    }
}
