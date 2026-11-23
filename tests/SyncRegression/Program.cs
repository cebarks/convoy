using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Convoy;

internal static class Program
{
    private const string Old = "BepInEx/plugins/fixture/old.dll";
    private const string First = "BepInEx/plugins/fixture/new.dll";
    private const string Second = "BepInEx/plugins/fixture/second.dll";
    private const string Unrelated = "BepInEx/plugins/unrelated/config.txt";
    private const string Bundle = "SPT_Runtime/user/cache/bundles/fixture.bundle";

    private static int Main()
    {
        using var server = new DownloadFixture();
        var requestHandler = Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!;
        requestHandler.GetField("Host")!.SetValue(null, server.Url);
        requestHandler.GetField("SessionId")!.SetValue(null, "convoy-regression");
        var tests = new (string Name, Action<GameFixture> Run)[]
        {
            ("partial sync", game => RejectSync(game, Zip((First, "verified first")))),
            ("corrupt sync", game => RejectSync(game, Zip((First, "wrong"), (Second, "verified second")))),
            ("duplicate entry", game => RejectSync(game,
                Zip((First, "verified first"), (Second, "verified second"), (First, "verified first")))),
            ("complete sync", CompleteSyncUsesVerifiedPathsOnly),
            ("partial full redownload", game => RejectRedownload(game, false,
                Zip((First, "verified first"), (Second, "verified second")))),
            ("partial bundle redownload", game => RejectRedownload(game, true, Zip())),
            ("complete bundle redownload", CompleteBundleRedownload),
        };

        var failed = 0;
        foreach (var test in tests)
        {
            using var game = new GameFixture(server);
            try
            {
                test.Run(game);
                Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} Convoy sync regressions passed");
        return failed == 0 ? 0 : 1;
    }

    private static void RejectSync(GameFixture game, byte[] payload)
    {
        var result = game.Sync(payload);
        Check(result == SyncResult.Failed
              && game.ReadIfPresent(Old) == "installed old file"
              && game.ReadIfPresent(First) == null
              && game.ReadIfPresent(Second) == null
              && game.ReadState() == game.OriginalState,
            $"rejected sync must not write or delete files or state (result {result}, old file present: {game.ReadIfPresent(Old) != null})");
    }

    private static void CompleteSyncUsesVerifiedPathsOnly(GameFixture game)
    {
        var result = game.Sync(Zip((First, "verified first"), (Second, "verified second"),
            (Unrelated, "unadvertised content")));
        Check(result == SyncResult.RestartRequired, $"complete sync returned {result}");
        Check(game.ReadIfPresent(First) == "verified first" && game.ReadIfPresent(Second) == "verified second",
            "verified files must match expected bytes");
        Check(game.ReadIfPresent(Old) == null, "superseded tracked file must be deleted");
        Check(game.ReadIfPresent(Unrelated) == "locally owned", "unadvertised entry overwrote unrelated file");
        Check(ConvoyState.Load().Mods[0].Version == "new", "successful sync must save state");
    }

    private static void RejectRedownload(GameFixture game, bool bundlesOnly, byte[] payload)
    {
        var result = game.Redownload(bundlesOnly, payload);
        Check(result == SyncResult.Failed
              && game.ReadIfPresent(Bundle) == null
              && game.ReadIfPresent(First) == null
              && game.ReadIfPresent(Second) == null
              && game.ReadState() == game.OriginalState,
            $"partial redownload must leave game and state unchanged (result {result})");
    }

    private static void CompleteBundleRedownload(GameFixture game)
    {
        var result = game.Redownload(true, Zip((Bundle, "bundle bytes")));
        Check(result == SyncResult.UpToDate && game.ReadIfPresent(Bundle) == "bundle bytes"
              && game.ReadIfPresent(First) == null && game.ReadState() == game.OriginalState,
            "valid bundle-only redownload should not require client DLLs or save mod state");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Zip(params (string Path, string Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                using var stream = zip.CreateEntry(entry.Path).Open();
                var bytes = Encoding.UTF8.GetBytes(entry.Bytes);
                stream.Write(bytes, 0, bytes.Length);
            }
        return buffer.ToArray();
    }

    private sealed class GameFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "convoy-regression-" + Guid.NewGuid().ToString("N"));
        private readonly DownloadFixture _server;
        private readonly SyncEngine _engine;
        private readonly ConvoyState _state;
        private readonly CatalogMod _newMod = new CatalogMod
        {
            Id = 1, Version = "new",
            FileChecksums = { [First] = "8026f2d9c37f23c8", [Second] = "927a136288d0655b" },
        };

        public string OriginalState { get; }

        public GameFixture(DownloadFixture server)
        {
            _server = server;
            Directory.CreateDirectory(_root);
            SetBepInExPath(nameof(Paths.GameRootPath), _root);
            SetBepInExPath(nameof(Paths.ConfigPath), Path.Combine(_root, "BepInEx", "config"));
            _engine = new SyncEngine(new ManualLogSource("Convoy sync regression"), null!);
            _state = new ConvoyState
            {
                ServerUrl = server.Url,
                Mods = { new ModState { Id = 1, Version = "old", Files = { new ModFileState { Path = Old } } } },
            };
            Write(Old, "installed old file");
            Write(Unrelated, "locally owned");
            _state.Save();
            OriginalState = ReadState();
        }

        public SyncResult Sync(byte[] payload)
        {
            _server.Payload = payload;
            var plan = new SyncPlan { ServerUrl = _server.Url, State = _state, WantedMods = { [1] = _newMod } };
            return _engine.ExecuteSync(plan, new List<int> { 1 }, new HashSet<int>());
        }

        public SyncResult Redownload(bool bundlesOnly, byte[] payload)
        {
            _server.Payload = payload;
            _newMod.BundleChecksums[Bundle] = "638833fba918a749";
            var catalog = new Catalog { Groups = { new CatalogGroup { Mods = { _newMod } } } };
            return _engine.ExecuteRedownload(catalog, bundlesOnly);
        }

        public string? ReadIfPresent(string path)
        {
            var full = Path.Combine(_root, path);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }

        public string ReadState() => File.ReadAllText(Path.Combine(Paths.ConfigPath, "Convoy", "state.json"));

        private void Write(string path, string bytes)
        {
            var full = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, bytes);
        }

        private static void SetBepInExPath(string property, string value) =>
            typeof(Paths).GetProperty(property)!.GetSetMethod(true)!.Invoke(null, new object[] { value });

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class DownloadFixture : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Thread _worker;
        public string Url { get; }
        public volatile byte[] Payload = Array.Empty<byte>();

        public DownloadFixture()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _worker = new Thread(Serve) { IsBackground = true };
            _worker.Start();
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext request;
                try { request = _listener.GetContext(); }
                catch (HttpListenerException) when (!_listener.IsListening) { return; }
                using var response = request.Response;
                request.Request.InputStream.CopyTo(Stream.Null);
                var path = request.Request.Url!.AbsolutePath;
                if (path == "/quma/convoy/download")
                {
                    var bytes = Payload;
                    response.ContentLength64 = bytes.Length;
                    response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else if (path == "/quma/convoy/report")
                    response.StatusCode = 204;
                else
                    response.StatusCode = 404;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _worker.Join();
            _listener.Close();
        }
    }
}
