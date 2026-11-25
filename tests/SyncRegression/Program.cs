using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Linq;
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
            ("file directory transition rejected before save failure", game => game.RollbackRegression(true, false)),
            ("failed state commit restores files and state", game => game.RollbackRegression(false, false)),
            ("rollback attempts every backup and reports failures", game => game.RollbackRegression(false, true)),
            ("duplicate catalog normal sync", game => game.DuplicateCatalog(-1)),
            ("duplicate catalog full redownload", game => game.DuplicateCatalog(0)),
            ("duplicate catalog bundle redownload", game => game.DuplicateCatalog(1)),
            ("duplicate unselected catalog before up-to-date", game => game.DuplicateCatalog(2)),
            ("partial sync", game => RejectSync(game, Zip((First, "verified first")))),
            ("corrupt sync", game => RejectSync(game, Zip((First, "wrong"), (Second, "verified second")))),
            ("duplicate entry", game => RejectSync(game,
                Zip((First, "verified first"), (Second, "verified second"), (First, "verified first")))),
            ("extra entry rejected", game => RejectSync(game, Zip((First, "verified first"), (Second, "verified second"), (Unrelated, "extra")))),
            ("managed matching remains unowned", game => game.ManagedMatching()),
            ("managed conflict preserves all bytes", game => game.ManagedConflict()),
            ("fresh dependency install/removal", game => game.ManagedLifecycle(false)),
            ("unowned dependency removal preserves repair", game => game.ManagedLifecycle(true)),
            ("modified owned dependency removal preserves edits", game => game.ManagedLifecycle(false, true)),
            ("shared dependency survives selection change", game => game.SharedDependency()),
            ("owned dependency update", game => game.DependencyUpdate(false)),
            ("modified owned dependency update conflict", game => game.DependencyUpdate(true)),
            ("unowned BepInEx conflict", game => game.UnownedPlugin()),
            ("case collision", game => game.CaseCollision()),
            ("ZIP symlink", game => game.ZipSymlink()),
            ("configuration preservation", game => game.PreserveConfiguration()),
            ("unsupported roots/path aliases rejected", game => game.RejectPaths()),
            ("reparse escape rejected", game => game.RejectLink()),
            ("same-version manifest upgrade and restart no-op", game => game.RestartPlan()),
            ("complete sync", CompleteSyncUsesVerifiedPathsOnly),
            ("partial full redownload", game => RejectRedownload(game, false,
                Zip((First, "verified first"), (Second, "verified second")))),
            ("partial bundle redownload", game => RejectRedownload(game, true, Zip())),
            ("complete bundle redownload", CompleteBundleRedownload),
        };

        var publisher = Environment.GetEnvironmentVariable("CONVOY_FIXTURE_EXPORT");
        if (!string.IsNullOrEmpty(publisher))
            tests = tests.Concat(new (string Name, Action<GameFixture> Run)[] {
                ("publisher batch fresh", game => game.PublisherDelivered(publisher!, 0, false)),
                ("publisher batch existing repaired", game => game.PublisherDelivered(publisher!, 0, true)),
                ("publisher single fresh", game => game.PublisherDelivered(publisher!, 1, false)),
                ("publisher single existing repaired", game => game.PublisherDelivered(publisher!, 1, true)),
            }).ToArray();
        var live = Environment.GetEnvironmentVariable("CONVOY_PUBLISHER_URL");
        if (!string.IsNullOrEmpty(live))
            tests = tests.Concat(new (string Name, Action<GameFixture> Run)[] {
                ("live Rust publisher fresh", game => game.PublisherDelivered(live!, 0, false)),
                ("live Rust publisher existing repaired", game => game.PublisherDelivered(live!, 0, true)),
            }).ToArray();
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
        var result = game.Sync(Zip((First, "verified first"), (Second, "verified second")));
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

    private static string Hash(string bytes)
    {
        var hash = new System.IO.Hashing.XxHash3();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(bytes))) hash.Append(stream);
        return hash.GetCurrentHashAsUInt64().ToString("x16");
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
            Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!.GetField("Host")!.SetValue(null, server.Url);
            Directory.CreateDirectory(_root);
            SetBepInExPath(nameof(Paths.GameRootPath), _root);
            SetBepInExPath(nameof(Paths.ConfigPath), Path.Combine(_root, "BepInEx", "config"));
            SetBepInExPath(nameof(Paths.BepInExConfigPath), Path.Combine(_root, "BepInEx", "config", "BepInEx.cfg"));
            _engine = new SyncEngine(new ManualLogSource("Convoy sync regression"),
                new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(_root, "BepInEx/config/test.cfg"), false)));
            _state = new ConvoyState
            {
                ServerUrl = server.Url,
                Mods = { new ModState { Id = 1, Version = "old", Files = { new ModFileState { Path = Old, Hash = Hash("installed old file") } } } },
            };
            Write(Old, "installed old file");
            Write(Unrelated, "locally owned");
            _state.Save();
            OriginalState = ReadState();
        }

        private const string Managed = "EscapeFromTarkov_Data/Managed/Unity.VectorGraphics.dll";

        public void RollbackRegression(bool transition, bool recoveryFailure)
        {
            var type = typeof(SyncEngine).Assembly.GetType("Convoy.SyncFiles", true)!;
            var transaction = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { _root, _state.Mods }, null)!;
            object? Call(string method, params object[] args) => type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transaction, args);
            var incoming = transition ? Old + "/child.dll" : First;
            var staging = Path.Combine(_root, "staging");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, incoming))!);
            File.WriteAllText(Path.Combine(staging, incoming), "verified first");
            Write(Second, "second original");
            var removed = _state.Mods.SelectMany(m => m.Files).Concat(new[] { new ModFileState { Path = Second, Hash = Hash("second original"), Owned = true } }).ToArray();
            Exception? failure = null;
            var saveCalled = false;
            try
            {
                Call("Preflight", new Dictionary<string, string> { [incoming] = Hash("verified first") }, Array.Empty<string>(), removed);
                Call("Apply", staging, (Action)(() => {
                    saveCalled = true;
                    if (recoveryFailure) Directory.CreateDirectory(Path.Combine(_root, Second));
                    throw new IOException("injected state save failure");
                }));
            }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
            Check(failure != null, "operation must fail");
            Check(ReadIfPresent(Old) == "installed old file" && !Directory.Exists(Path.Combine(_root, Old)), "owned file stranded after rollback");
            Check(ReadState() == OriginalState && ReadIfPresent(First) == null, "failed state commit mutated files/state");
            if (transition) Check(!saveCalled, "incompatible path shape must fail before mutations");
            else if (recoveryFailure)
            {
                Check(failure is AggregateException aggregate && aggregate.InnerExceptions.Count >= 2
                    && aggregate.InnerExceptions[0].Message.Contains("state save"), "original and recovery failures must both be reported");
                Check(Directory.GetFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Length == 1, "only failed recovery backup must remain");
            }
            else Check(saveCalled && ReadIfPresent(Second) == "second original"
                && Directory.GetFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Length == 0, "successful rollback must restore all backups");
        }

        public void DuplicateCatalog(int mode)
        {
            // Disjoint paths also exercise identity validation rather than only shared path hashes.
            var duplicate = new CatalogMod { Id = mode == 2 ? 99 : 1, Version = _newMod.Version, Name = _newMod.Name,
                FileChecksums = { [Unrelated] = Hash("conflicting bytes") },
                BundleChecksums = { [Bundle] = Hash("other bundle") } };
            _newMod.Id = duplicate.Id;
            if (mode >= 0) _newMod.BundleChecksums[Bundle] = Hash("bundle bytes");
            var catalog = new Catalog { Groups = { new CatalogGroup { Slug = "default", Tier = "required", Mods = { _newMod, duplicate } } } };
            _server.CatalogJson = Newtonsoft.Json.JsonConvert.SerializeObject(catalog);
            _server.Payload = mode == 1 ? Zip((Bundle, "bundle bytes")) : Zip((First, "verified first"), (Second, "verified second"), (Bundle, "bundle bytes"));
            var rejected = false;
            try
            {
                if (mode < 0) { _engine.PlanSync(); }
                else rejected = _engine.ExecuteRedownload(catalog, mode == 1) == SyncResult.Failed;
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && ReadIfPresent(Old) == "installed old file" && ReadIfPresent(Unrelated) == "locally owned"
                && ReadIfPresent(First) == null && ReadIfPresent(Bundle) == null && ReadState() == OriginalState,
                "inconsistent duplicate catalog must reject before files, deletions or state change");
        }

        public void ManagedMatching()
        {
            Write(Managed, "verified first");
            _newMod.FileChecksums[Managed] = "8026f2d9c37f23c8";
            var result = Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first")));
            Check(result == SyncResult.RestartRequired && ReadIfPresent(Managed) == "verified first", "matching dependency must be usable");
            Check(ReadState().Contains("\"owned\": false"), "pre-existing matching dependency was silently adopted");
        }

        public void ManagedConflict()
        {
            Write(Managed, "base game assembly");
            _newMod.FileChecksums[Managed] = "8026f2d9c37f23c8";
            var result = Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first")));
            Check(result == SyncResult.Failed && ReadIfPresent(Managed) == "base game assembly"
                && ReadIfPresent(Old) == "installed old file" && ReadState() == OriginalState, "unowned dependency conflict mutated game or state");
        }

        public void PublisherDelivered(string publisher, int route, bool repaired)
        {
            if (publisher.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
            {
                Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!.GetField("Host")!.SetValue(null, publisher);
                using var client = new WebClient();
                _server.CatalogJson = client.DownloadString(publisher + "/quma/convoy/catalog");
                var remote = Newtonsoft.Json.JsonConvert.DeserializeObject<Catalog>(_server.CatalogJson)!;
                _server.Payload = client.DownloadData(publisher + "/quma/convoy/mod/" + remote.Groups.Single().Mods.Single().Id + "/archive");
            }
            else
            {
                _server.CatalogJson = File.ReadAllText(Path.Combine(publisher, "catalog.json"));
                _server.Payload = File.ReadAllBytes(Path.Combine(publisher, $"archive-{route}.zip"));
            }
            var catalog = Newtonsoft.Json.JsonConvert.DeserializeObject<Catalog>(_server.CatalogJson)!;
            var mod = catalog.Groups.Single().Mods.Single();
            new ConvoyState().Save();
            if (repaired)
            {
                using var zip = new ZipArchive(new MemoryStream(_server.Payload));
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("EscapeFromTarkov_Data/Managed/")))
                {
                    var target = Path.Combine(_root, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = entry.Open();
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                }
            }
            var plan = _engine.PlanSync()!;
            Check(_engine.ExecuteSync(plan, plan.Installs.Select(m => m.Id).ToList(), new HashSet<int>()) == SyncResult.RestartRequired, "publisher delivery failed");
            foreach (var file in mod.FileChecksums)
            {
                var hasher = new System.IO.Hashing.XxHash3();
                using var stream = File.OpenRead(Path.Combine(_root, file.Key));
                hasher.Append(stream);
                Check(hasher.GetCurrentHashAsUInt64().ToString("x16") == file.Value, "delivered checksum mismatch");
            }
            var dependencies = ConvoyState.Load().Mods.Single().Files.Where(f => f.Path.StartsWith("EscapeFromTarkov_Data/Managed/")).ToList();
            Check(dependencies.Count == 2 && dependencies.All(f => f.Owned == !repaired), "dependency count or repaired ownership mismatch");
            var restart = _engine.PlanSync()!;
            Check(restart.Installs.Count == 0 && restart.Updates.Count == 0 && restart.Removals.Count == 0, "publisher restart not no-op");
        }

        public void ManagedLifecycle(bool preexisting, bool modified = false)
        {
            if (preexisting) Write(Managed, "verified first");
            _newMod.FileChecksums[Managed] = Hash("verified first");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first"))) == SyncResult.RestartRequired, "dependency install failed");
            var saved = ConvoyState.Load();
            if (modified) Write(Managed, "local edit");
            var plan = new SyncPlan { State = saved, ServerUrl = _server.Url, Removals = { new PlannedMod { Id = 1 } } };
            Check(_engine.ExecuteSync(plan, new List<int>(), new HashSet<int>()) == SyncResult.RestartRequired, "removal failed");
            Check(ReadIfPresent(Managed) == (modified ? "local edit" : preexisting ? "verified first" : null), "dependency ownership removal mismatch");
        }

        public void SharedDependency()
        {
            _newMod.FileChecksums[Managed] = Hash("verified first");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first"))) == SyncResult.RestartRequired, "initial install");
            var state = ConvoyState.Load();
            state.Mods.Add(new ModState { Id = 2, Version = "new", Files = { new ModFileState { Path = Managed, Hash = Hash("verified first"), Owned = true } } });
            var mod = new CatalogMod { Id = 2, Version = "new", FileChecksums = { [Managed] = Hash("verified first") } };
            var plan = new SyncPlan { State = state, ServerUrl = _server.Url, WantedMods = { [2] = mod }, Removals = { new PlannedMod { Id = 1 } } };
            Check(_engine.ExecuteSync(plan, new List<int>(), new HashSet<int>()) == SyncResult.RestartRequired && ReadIfPresent(Managed) == "verified first", "shared dependency deleted");
        }

        public void DependencyUpdate(bool modified)
        {
            _newMod.FileChecksums[Managed] = Hash("verified first");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first"))) == SyncResult.RestartRequired, "initial dependency install");
            if (modified) Write(Managed, "personal assembly");
            var before = ReadState();
            var saved = ConvoyState.Load();
            _newMod.FileChecksums[Managed] = Hash("updated dependency");
            _server.Payload = Zip((First, "verified first"), (Second, "verified second"), (Managed, "updated dependency"));
            var plan = new SyncPlan { State = saved, ServerUrl = _server.Url, WantedMods = { [1] = _newMod } };
            var result = _engine.ExecuteSync(plan, new List<int> { 1 }, new HashSet<int>());
            Check(result == (modified ? SyncResult.Failed : SyncResult.RestartRequired), "update conflict result");
            Check(ReadIfPresent(Managed) == (modified ? "personal assembly" : "updated dependency"), "update bytes");
            if (modified) Check(ReadState() == before, "failed update changed state");
        }

        public void UnownedPlugin()
        {
            Write(First, "unrelated plugin");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"))) == SyncResult.Failed
                && ReadIfPresent(First) == "unrelated plugin" && ReadState() == OriginalState, "unowned plugin overwritten");
        }

        public void CaseCollision()
        {
            _newMod.FileChecksums["BepInEx/plugins/fixture/NEW.dll"] = Hash("verified first");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), ("BepInEx/plugins/fixture/NEW.dll", "verified first"))) == SyncResult.Failed && ReadState() == OriginalState, "case collision accepted");
        }

        public void ZipSymlink()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry(First);
                entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes("verified first");
                stream.Write(bytes, 0, bytes.Length);
            }
            Check(Sync(buffer.ToArray()) == SyncResult.Failed && ReadState() == OriginalState, "symlink archive accepted");
        }

        public void PreserveConfiguration()
        {
            const string config = "BepInEx/config/fixture.cfg";
            Write(config, "personal settings");
            _newMod.FileChecksums[config] = Hash("defaults");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (config, "defaults"))) == SyncResult.RestartRequired
                && ReadIfPresent(config) == "personal settings", "config overwritten");
        }

        public void RejectPaths()
        {
            var invalid = new[] { "../escape.dll", "/absolute.dll", "C:/drive.dll", "BepInEx/../escape.dll", "BepInEx//alias.dll", "BepInEx/CON.dll", "BepInEx/plugins/a.dll.", "EscapeFromTarkov_Data/Managed/a.txt", "EscapeFromTarkov_Data/Managed/sub/a.dll", "SPT_Runtime/user/profiles/private.json", "EscapeFromTarkov_Data\\Managed\\a.dll" };
            foreach (var path in invalid)
            {
                _newMod.FileChecksums[path] = Hash("payload");
                Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (path, "payload"))) == SyncResult.Failed
                    && ReadState() == OriginalState && ReadIfPresent(Old) == "installed old file", "unsafe path mutated game: " + path);
                _newMod.FileChecksums.Remove(path);
            }
        }

        public void RejectLink()
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.Combine(_root, "EscapeFromTarkov_Data"));
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln", "-s " + outside + " " + Path.Combine(_root, "EscapeFromTarkov_Data/Managed")) { UseShellExecute = false });
            process!.WaitForExit();
            Check(process.ExitCode == 0, "link fixture failed");
            _newMod.FileChecksums[Managed] = Hash("verified first");
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"), (Managed, "verified first"))) == SyncResult.Failed
                && ReadState() == OriginalState && !File.Exists(Path.Combine(outside, "Unity.VectorGraphics.dll")), "link escaped");
        }

        public void RestartPlan()
        {
            Check(Sync(Zip((First, "verified first"), (Second, "verified second"))) == SyncResult.RestartRequired, "initial install");
            _server.CatalogJson = Newtonsoft.Json.JsonConvert.SerializeObject(new Catalog { Groups = { new CatalogGroup { Tier = "required", Mods = { _newMod } } } });
            var noOp = _engine.PlanSync()!;
            Check(noOp.Installs.Count == 0 && noOp.Updates.Count == 0 && noOp.Removals.Count == 0, "restart was not no-op");
            _newMod.FileChecksums[Managed] = Hash("verified first");
            _server.CatalogJson = Newtonsoft.Json.JsonConvert.SerializeObject(new Catalog { Groups = { new CatalogGroup { Tier = "required", Mods = { _newMod } } } });
            var upgrade = _engine.PlanSync()!;
            Check(upgrade.Updates.Count == 1, "same-version new dependency not planned");
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
        public string CatalogJson = "{}";

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
                else if (path == "/quma/convoy/catalog")
                {
                    var bytes = Encoding.UTF8.GetBytes(CatalogJson);
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
