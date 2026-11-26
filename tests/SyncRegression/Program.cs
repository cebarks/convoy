using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Convoy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private const long Id = 2147483648L;
    private const string Old = "BepInEx/plugins/fixture/old.dll";
    private const string First = "BepInEx/plugins/fixture/new.dll";
    private const string Second = "BepInEx/plugins/fixture/second.dll";
    private const string Unrelated = "BepInEx/plugins/unrelated/config.txt";
    private const string Bundle = "SPT_Runtime/user/cache/bundles/fixture.bundle";
    private const string Managed = "EscapeFromTarkov_Data/Managed/Unity.VectorGraphics.dll";
    private const string Config = "BepInEx/config/fixture.cfg";
    private const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string LargeDigest = "3b6a07d0d404fab4e23b6d34bc6696a6a312dd92821332385e5af7c01c421351";
    private const int LargeLength = 64 * 1024 * 1024;

    private static int Main()
    {
        var childRoot = Environment.GetEnvironmentVariable("CONVOY_CHILD_ROOT");
        if (childRoot != null) return GameFixture.Child(childRoot);
        using var server = new DownloadFixture();
        var handler = Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!;
        handler.GetField("SessionId")!.SetValue(null, "convoy-regression-fixture");
        var tests = new List<(string Name, Action<GameFixture> Run)>
        {
            ("v2 resumes only unfinished object", g => g.Resume()),
            ("v2 second process does not acquire root", g => g.Exclusive()),
            ("v2 staging cap", g => g.Cap()),
            ("sixteen object HTTP bodies overlap without changing the default pool", g => g.ConcurrentObjects()),
            ("v2 cancelled final GET preserves live files and state", g => g.Cancel(false)),
            ("v2 cancelled bookkeeping preserves state", g => g.Cancel(true)),
            ("v2 stale generation requests replan", g => g.StaleGeneration()),
            ("v2 rejected ranges preserve other staging", g => g.RangeFaults()),
            ("v2 full response replaces partial", g => g.ReplacePartial()),
            ("v2 unsolicited range is rejected", g => g.BadObject("unsolicited")),
            ("v2 complete persisted object is verified locally", g => g.Checkpoint("complete")),
            ("v2 corrupt complete checkpoint is not accepted", g => g.Checkpoint("corrupt")),
            ("v2 shortened checkpoint restarts", g => g.Checkpoint("shortened")),
            ("v2 forged checkpoint restarts", g => g.Checkpoint("forged")),
            ("v2 real 416 restarts without appending", g => g.Checkpoint("416")),
            ("v2 preparing can be cancelled", g => g.Preparing()),
            ("v2 busy retries honor bounded retry-after", g => g.RetryAfter()),
            ("v2 game volume space reserved before downloading", g => g.FreeSpace()),
            ("v2 retries discard corrupt attempt progress", g => g.DiscardedProgress()),
            ("v2 cancelled full repair preserves live files and state", g => g.CancelRepair(false)),
            ("v2 cancelled bundle repair preserves live files and state", g => g.CancelRepair(true)),
            ("r1 linked full fallback", g => g.Linked("full")),
            ("r1 linked resume", g => g.Linked("resume")),
            ("r1 linked stable lock", g => g.Linked("lock")),
            ("r1 final busy", g => g.Publication("busy", false)),
            ("r1 final preparing same", g => g.Publication("preparing", false)),
            ("r1 final preparing changed", g => g.Publication("preparing", true)),
            ("r1 final disabled", g => g.Publication("disabled", false)),
            ("r1 final publish_failed", g => g.Publication("publish_failed", false)),
            ("r1 object preparing same", g => g.ObjectPreparing(false)),
            ("r1 object preparing changed", g => g.ObjectPreparing(true)),
            ("r1 removal-only foreign volume", g => g.ForeignRemoval()),
            ("r1 stale full repair explicit retry", g => g.StaleRepair(false)),
            ("r1 stale bundle repair explicit retry", g => g.StaleRepair(true)),
            ("r1 cancel blocked object read", g => g.CancelBody()),
            ("r1 cancel local hash", g => g.CancelHash()),
            ("parallel local scan bounds and overlaps", g => g.ConcurrentLocalReads(precommit: false)),
            ("parallel local precommit bounds and overlaps", g => g.ConcurrentLocalReads(precommit: true)),
            ("parallel local cancellation joins readers", g => g.ConcurrentLocalReads(precommit: true, cancel: true)),
            ("r1 cancel object retry wait", g => g.CancelRetry()),
            ("r1 panel writer contends and retries against new ownership", g => g.PanelContender()),
            ("r1 final error body shares deadline", g => g.ErrorBodyDeadline()),
            ("r2 preparation window shared across digests", g => g.ApplyBudget("digest")),
            ("r2 final preparing cannot restart window", g => g.ApplyBudget("final")),
            ("r2 final exact 304 after elapsed window", g => g.ApplyBudget("healthy")),
            ("r2 conditional transient allowance shared across digests", g => g.ApplyBudget("transient-digest")),
            ("r2 conditional transient allowance shared with final", g => g.ApplyBudget("transient-final")),
            ("r2 concurrent conditional retries share three slots", g => g.ConcurrentBudget()),
            ("r2 disk capacity renewed after final 304", g => g.FinalAdmission(false)),
            ("r2 removal volume renewed after final 304", g => g.FinalAdmission(true)),
            ("v2 fresh install", g => g.Install(false, false)),
            ("v2 matching install downloads nothing", g => g.Install(true, true)),
            ("v2 mostly matching install downloads only missing asset", g => g.Install(true, false)),
            ("v2 ordinary no-op", g => g.NoOp()),
            ("v2 state authority changed after planning", g => g.StaleState(false)),
            ("v2 absent state appeared after planning", g => g.StaleState(true)),
            ("v2 state changes during publication check abort", g => g.ConditionalState(false)),
            ("v2 bundle repair checks state after publication", g => g.ConditionalState(true)),
            ("v2 no-op bookkeeping checks state after publication", g => g.Bookkeeping()),
            ("v2 verifies local bytes before conditional request", g => g.LocalValidationOrder()),
            ("v2 rejects absent mandatory directory asset", g => g.MandatoryDirectory()),
            ("v2 bounded large delivery", g => g.LargeDelivery()),
            ("v2 rejects parent-file alias", g => g.BadManifest(m => m.Add(Second + "/child.dll", "def"))),
            ("v2 rejects case alias", g => g.BadManifest(m => m.Add(Second.ToUpperInvariant().Replace("BEPINEX/", "BepInEx/"), "abc"))),
            ("v2 rejects aliased parents", g => g.BadManifest(m => m.Add("BepInEx/plugins/Fixture/child.dll", "abc"))),
            ("v2 rejects duplicate IDs", g => g.InvalidCatalog((doc, _) => ((JArray)doc["groups"]![0]!["mods"]!).Add(doc["groups"]![1]!["mods"]![0]!.DeepClone()))),
            ("v2 rejects duplicate JSON properties", g => g.InvalidCatalog(null, json => json.Replace("\"protocol\":2", "\"protocol\":2,\"protocol\":2"))),
            ("v2 rejects non-JSON numeric spelling", g => g.InvalidCatalog(null, json => json.Replace("\"protocol\":2", "\"protocol\":02"))),
            ("v2 rejects single quoted JSON", g => g.InvalidCatalog(null, json => json.Replace("\"protocol\":2", "'protocol':2"))),
            ("v2 rejects non-integer length", g => g.InvalidCatalog((doc, _) => doc["groups"]![1]!["mods"]![0]!["files"]![0]!["length"] = 3.0)),
            ("v2 rejects wrong manifest validator", g => g.InvalidCatalog((doc, _) => doc["generation"] = new string('f', 64))),
            ("v2 rejects wrong origin", g => g.WrongOrigin("http://127.0.0.1:1")),
            ("v2 rejects unknown origin", g => g.WrongOrigin("")),
            ("v2 update retires owned file", g => g.Retire()),
            ("v2 corrupt response preserves prior install", g => g.BadObject("corrupt")),
            ("v2 truncated response preserves prior install", g => g.BadObject("truncated")),
            ("v2 invalid object validator preserves prior install", g => g.BadObject("validator")),
            ("v2 encoded object rejected", g => g.BadObject("encoding")),
            ("v2 object redirect rejected", g => g.BadObject("redirect")),
            ("v2 generation change after staging aborts apply", g => g.BadObject("generation")),
            ("v2 file changes before state-only apply abort", g => g.ChangedBeforeApply()),
            ("file directory transition rejected before save failure", g => g.RollbackRegression(true, false)),
            ("failed state commit restores files and state", g => g.RollbackRegression(false, false)),
            ("rollback attempts every backup and reports failures", g => g.RollbackRegression(false, true)),
            ("managed matching remains unowned", g => g.ManagedLifecycle(true)),
            ("managed conflict preserves all bytes", g => g.Conflict(Managed)),
            ("fresh dependency install/removal", g => g.ManagedLifecycle(false)),
            ("modified owned dependency removal preserves edits", g => g.ManagedLifecycle(false, true)),
            ("shared dependency survives selection change", g => g.SharedDependency()),
            ("declined revision protects incompatible shared bytes", g => g.SharedDependency(true)),
            ("owned dependency update", g => g.DependencyUpdate(false)),
            ("modified owned dependency update conflict", g => g.DependencyUpdate(true)),
            ("unowned BepInEx conflict", g => g.Conflict(First)),
            ("configuration preservation", g => g.PreserveConfiguration()),
            ("unsupported roots/path aliases rejected", g => g.RejectPaths()),
            ("linked game ancestor permits sync", g => g.LinkedGameAncestor()),
            ("reparse escape rejected", g => g.RejectLink()),
            ("same-version manifest update is planned", g => g.SameVersionUpdate()),
            ("task5 approved replacement remains unowned", g => g.Recover("approved")),
            ("task5 replacement decline preserves install", g => g.Recover("declined")),
            ("task5 edited preimage rejects consent", g => g.Recover("edited")),
            ("task5 mixed bundle revision refused", g => g.MixedBundle()),
            ("task5 matching full repair fetches", g => g.MatchingRepair(false)),
            ("task5 matching bundle repair fetches", g => g.MatchingRepair(true)),
            ("partial full redownload", g => g.Repair(false, true)),
            ("partial bundle redownload", g => g.Repair(true, true)),
            ("complete bundle redownload", g => g.Repair(true, false)),
            ("complete full redownload", g => g.Repair(false, false)),
        };
        foreach (var mode in new[] { "origin", "generation", "mod", "path", "prior", "next", "now-next", "missing", "final-edit", "backup-io", "state-io", "rollback-io", "cancel", "cancel-progress" })
            tests.Add(("task5 recovery " + mode, g => g.Recover(mode)));
        foreach (var mode in new[] { "legacy", "missing-record", "managed", "non-dll", "config", "another-mod", "shared-retained", "forged-removal", "full-repair", "bundle-repair" })
            tests.Add(("task5 forbidden recovery " + mode, g => g.ForbiddenRecovery(mode)));
        foreach (var mode in new[] { "client", "bundle", "config", "missing-mod", "missing-record", "legacy" })
            tests.Add(("task5 installed-set bundle proof " + mode, g => g.BundleProof(mode)));
        foreach (var bundlesOnly in new[] { false, true })
            tests.Add(("task5 force matching unowned " + bundlesOnly, g => g.ForceUnowned(bundlesOnly)));
        foreach (var mode in new[] { "volume", "original-bytes", "final-304" })
            tests.Add(("task5 recovery admission " + mode, g => g.RecoveryAdmission(mode)));
        tests.Add(("task5 full repair resumes partial and preserves unrelated staging", g => g.RepairResume()));
        foreach (var bundlesOnly in new[] { false, true })
            foreach (var withProgress in new[] { false, true })
                tests.Add(($"task5 external repair cancellation {bundlesOnly}/{withProgress}", g => g.ExternalRepairCancel(bundlesOnly, withProgress)));
        tests.Add(("r1 task5 swapped genuine catalog", g => g.SwappedPlan(false)));
        tests.Add(("r1 task5 pinned origin", g => g.SwappedPlan(true)));
        tests.Add(("r1 task5 appeared config preserved", g => g.Appeared(true)));
        tests.Add(("r1 task5 appeared DLL rejected", g => g.Appeared(false)));
        tests.Add(("v2 prepared install does not reopen the transfer cache", g => g.PreparedInstall(true)));
        if (File.Exists("/proc/self/io"))
            tests.Add(("v2 prepared install does not bulk-write during apply", g => g.PreparedInstall(false)));
        tests.Add(("v2 staged truncation cannot change installed bytes", g => g.StagedDrift("sh")));
        tests.Add(("v2 staged same-length drift cannot change installed bytes", g => g.StagedDrift("bad bytes")));
        tests.Add(("Wave republish during recovery replan", g => g.PostHashAdmission(true)));
        tests.Add(("Wave capacity drop during recovery", g => g.PostHashAdmission(false)));
        tests.Add(("r1 task5 bundle panel dispatch preserves dirty delta", g => g.DirtyBundleDispatch()));
        var live = Environment.GetEnvironmentVariable("CONVOY_PUBLISHER_URL");
        if (!string.IsNullOrEmpty(live))
        {
            PrintAssemblies();
            tests.Add(("live Rust publisher fresh", g => g.Live(live!, false)));
            tests.Add(("live Rust publisher matching", g => g.Live(live!, true)));
        }
        tests.Add(("old publisher requires paired upgrade", g => g.OldPublisher(false)));
        tests.Add(("old publisher auth redirect requires paired upgrade", g => g.OldPublisher(true)));
        var filter = Environment.GetEnvironmentVariable("CONVOY_CASE");
        if (!string.IsNullOrEmpty(filter)) tests.RemoveAll(t => t.Name != filter);
        if (tests.Count == 0)
        {
            Console.Error.WriteLine($"Unknown CONVOY_CASE: {filter}");
            return 1;
        }
        var failed = 0;
        foreach (var test in tests)
        {
            using var game = new GameFixture(server);
            try { test.Run(game); Console.WriteLine($"PASS: {test.Name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex}"); }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} Convoy sync regressions passed");
        return failed == 0 ? 0 : 1;
    }

    private static void PrintAssemblies()
    {
        Console.WriteLine($"ASSEMBLY {typeof(SyncEngine).Assembly.Location}");
        Console.WriteLine($"HASHING {typeof(XxHash3).Assembly.Location}");
        Console.WriteLine($"HARNESS {Assembly.GetExecutingAssembly().Location}");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static string Sha(string bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(bytes)));
    }

    private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    private static string Hash(string bytes) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(bytes)).ToString("x16");

    private static DeliveryAsset MakeAsset(string path, string data) => (DeliveryAsset)Activator.CreateInstance(typeof(DeliveryAsset),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { path, (long)Encoding.UTF8.GetByteCount(data), Sha(data), Hash(data) }, null)!;

    private sealed class ManifestMod
    {
        public long Id;
        public string Version = "new";
        public bool Optional;
        public List<(string Path, string Bytes)> Assets = new List<(string Path, string Bytes)>();
        public ManifestMod Add(string path, string bytes) { Assets.Add((path, bytes)); return this; }
    }

    private sealed class HeldWave : IDisposable
    {
        private readonly CountdownEvent _ready;
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private int _entered;
        public HeldWave(int size) => _ready = new CountdownEvent(size);
        public int Entered => Volatile.Read(ref _entered);
        public void Hold()
        {
            if (_release.IsSet) return;
            if (Interlocked.Increment(ref _entered) <= _ready.InitialCount) _ready.Signal();
            Check(_release.Wait(20_000), "held wave was not released");
        }
        public void WaitFor(string operation) => Check(_ready.Wait(8_000),
            $"expected {_ready.InitialCount} simultaneous {operation}; observed {Entered}");
        public void Release() => _release.Set();
        public void Dispose() { _ready.Dispose(); _release.Dispose(); }
    }

    private sealed class GameFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "convoy-regression-" + Guid.NewGuid().ToString("N"));
        private readonly DownloadFixture _server;
        private readonly SyncEngine _engine;
        private readonly ConvoyState _state;
        private string StateFile => Path.Combine(Paths.ConfigPath, "Convoy", "state.json");
        public string OriginalState { get; }

        public GameFixture(DownloadFixture server)
        {
            _server = server;
            _server.Reset();
            Host(server.Url);
            Directory.CreateDirectory(_root);
            SetBepInExPath(nameof(Paths.GameRootPath), _root);
            SetBepInExPath(nameof(Paths.ConfigPath), Path.Combine(_root, "BepInEx", "config"));
            SetBepInExPath(nameof(Paths.BepInExConfigPath), Path.Combine(_root, "BepInEx", "config", "BepInEx.cfg"));
            _engine = new SyncEngine(new ManualLogSource("Convoy sync regression"),
                new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(_root, "BepInEx/config/test.cfg"), false)));
            _state = new ConvoyState { ServerUrl = server.Url, EnabledGroups = { "optional" }, Mods = {
                new ModState { Id = Id, Version = "old", Files = { new ModFileState { Path = Old, Hash = Hash("installed old file") } } } } };
            Write(Old, "installed old file");
            Write(Unrelated, "locally owned");
            _state.Save();
            OriginalState = ReadState();
        }

        private static void Host(string value) => Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!.GetField("Host")!.SetValue(null, value);
        private static ManifestMod Required() => new ManifestMod { Id = Id }.Add(First, "abc");
        private static ManifestMod Optional(long id = Id + 1) => new ManifestMod { Id = id, Optional = true };
        private void Fresh() { File.Delete(StateFile); File.Delete(Path.Combine(_root, Old)); }
        private SyncPlan Plan() => _engine.PlanSync() ?? throw new InvalidOperationException("No plan returned");
        private SyncResult Run(SyncPlan plan, params long[] decline) => _engine.ExecuteSync(plan,
            plan.Installs.Concat(plan.Updates).Select(m => m.Id).Except(decline).ToList(), new HashSet<long>(decline), Array.Empty<ReplacementConsent>());
        private SyncResult Run() => Run(Plan());

        public static int Child(string root)
        {
            PrintAssemblies();
            var url = Environment.GetEnvironmentVariable("CONVOY_CHILD_URL")!;
            Check(new Uri(url).IsLoopback, "child requires loopback");
            Host(url);
            Assembly.Load("spt-common").GetType("SPT.Common.Http.RequestHandler", true)!
                .GetField("SessionId")!.SetValue(null, "convoy-regression-fixture");
            SetBepInExPath(nameof(Paths.GameRootPath), root);
            SetBepInExPath(nameof(Paths.ConfigPath), Path.Combine(root, "BepInEx", "config"));
            SetBepInExPath(nameof(Paths.BepInExConfigPath), Path.Combine(root, "BepInEx/config/BepInEx.cfg"));
            var engine = new SyncEngine(new ManualLogSource("Child"),
                new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(root, "BepInEx/config/test.cfg"), false)));
            try
            {
                if (Environment.GetEnvironmentVariable("CONVOY_PANEL_CHILD") == "1")
                {
                    SavePanelDelta(engine);
                    return 0;
                }
                var plan = engine.PlanSync()!;
                var result = engine.ExecuteSync(plan, plan.Updates.Concat(plan.Installs).Select(m => m.Id).ToList(), new HashSet<long>(), Array.Empty<ReplacementConsent>(), new SyncProgress());
                Console.WriteLine($"child {Process.GetCurrentProcess().Id}: {result}");
                return result == SyncResult.Failed ? 11 : result == SyncResult.RestartRequired ? 0 : result == SyncResult.Replan ? 15 : 12;
            }
            catch (Exception ex) when ((ex is TargetInvocationException ? ex.InnerException : ex) is IOException io)
            { Console.WriteLine(io.Message); return io.Message.Contains("lock Convoy game root") ? 13 : 14; }
        }

        private Process StartChild(bool panel = false)
        {
            var info = new ProcessStartInfo("mono", "\"" + Assembly.GetExecutingAssembly().Location + "\" '-config={\"BackendUrl\":\"http://127.0.0.1:1\",\"Version\":\"live\"}' -token=convoy-regression-fixture") { UseShellExecute = false };
            info.EnvironmentVariables["CONVOY_PANEL_CHILD"] = panel ? "1" : "0";
            info.EnvironmentVariables["CONVOY_CHILD_ROOT"] = _root;
            info.EnvironmentVariables["CONVOY_CHILD_URL"] = _server.Url;
            return Process.Start(info)!;
        }
        private static int Finish(Process child)
        {
            if (!child.WaitForExit(25_000)) { child.Kill(); throw new IOException("Fixture child timed out"); }
            return child.ExitCode;
        }
        private void Unchanged() => Check(ReadState() == OriginalState && ReadIfPresent(First) == null
            && ReadIfPresent(Second) == null && ReadIfPresent(Old) == "installed old file", "interruption mutated the install");
        private SyncResult Execute(SyncPlan plan, SyncProgress progress) => _engine.ExecuteSync(plan,
            plan.Installs.Concat(plan.Updates).Select(m => m.Id).ToList(), new HashSet<long>(), Array.Empty<ReplacementConsent>(), progress);

        private void SeedPartial()
        {
            _server.Publish(Required().Add(Second, "def"));
            _server.FaultDigest = Abc;
            _server.Fault = "truncated";
            using var child = StartChild();
            Check(Finish(child) == 11, "expected interrupted first process");
            Unchanged();
            _server.ExpectObjects(Abc, Abc, Abc, Abc, Sha("def"));
            Check(_server.Ranges.Where(r => r != "|").SequenceEqual(Enumerable.Repeat("bytes=2-|\"" + Abc + "\"", 3)), "wrong initial retry headers");
            _server.Fault = "";
            _server.ClearRequests();
        }

        public void Resume()
        {
            SeedPartial();
            var progress = new SyncProgress();
            Check(Execute(Plan(), progress) == SyncResult.RestartRequired, "reopen failed");
            _server.ExpectObjects(Abc);
            Check(_server.Ranges.ToArray().SequenceEqual(new[] { "bytes=2-|\"" + Abc + "\"" }), "wrong resume headers");
            Check(Sha(ReadIfPresent(First)!) == Abc && ReadIfPresent(Second) == "def", "reopened bytes differ");
            var view = progress.Snapshot();
            Check(view.Reused == 5 && view.Received == 1 && view.Planned == 6 && !progress.TryCancel(), "wrong unique progress or apply boundary");
            Console.WriteLine("resume wire: child full GET, 3 range retries, verified sibling GET once; reopen range 2-, 1 received / 5 reused");
        }

        public void Exclusive()
        {
            _server.Publish(Required());
            using var requested = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            _server.ObjectRequested = () => { requested.Set(); release.Wait(TimeSpan.FromSeconds(20)); };
            using var first = StartChild();
            try
            {
                Check(requested.Wait(TimeSpan.FromSeconds(10)), "holder never reached object GET");
                using var second = StartChild();
                Check(Finish(second) == 13, "second process did not observe exclusive root lock");
                Unchanged();
                Check(!first.HasExited && first.Id != second.Id, "children did not overlap");
                Console.WriteLine($"exclusive process PIDs: {first.Id}, {second.Id}");
            }
            finally { release.Set(); }
            Check(Finish(first) == 0, "holder did not finish");
            _server.ExpectObjects(Abc);
        }

        public void Cap()
        {
            _server.Publish(Required());
            var config = new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(_root, "BepInEx/config/cap.cfg"), false)) { SpoolMaxBytes = 2 };
            var engine = new SyncEngine(new ManualLogSource("Cap"), config);
            var progress = new SyncProgress();
            Check(engine.ExecuteSync(engine.PlanSync()!, new List<long> { Id }, new HashSet<long>(), Array.Empty<ReplacementConsent>(), progress) == SyncResult.Failed
                && progress.Snapshot().Error!.Contains("spool cap"), "cap was not enforced");
            Unchanged();
            _server.ExpectObjects();
        }

        public void Cancel(bool bookkeeping)
        {
            _server.Publish(Required());
            var plan = Plan();
            if (bookkeeping)
            {
                Check(Run(plan) == SyncResult.RestartRequired, "initial apply failed");
                var state = ConvoyState.Load();
                state.EnabledGroups.Clear();
                state.Save();
                _server.Publish(Required(), Optional().Add(Second, "def"));
            }
            var before = ReadState();
            var progress = new SyncProgress();
            using var requested = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            _server.Conditional = () => { requested.Set(); release.Wait(TimeSpan.FromSeconds(15)); };
            var worker = Task.Run(() =>
            {
                if (!bookkeeping) return Execute(plan, progress);
                try { _engine.PlanSync(progress); return SyncResult.UpToDate; }
                catch (Exception) when (!progress.Snapshot().CanCancel) { return SyncResult.Cancelled; }
            });
            try
            {
                Check(requested.Wait(TimeSpan.FromSeconds(10)) && progress.TryCancel(), "cannot cancel during final GET");
            }
            finally { release.Set(); }
            Check(worker.Wait(10_000) && worker.Result == SyncResult.Cancelled && ReadState() == before
                && ReadIfPresent(Second) == null, "cancelled final GET changed state");
            if (!bookkeeping) Unchanged();
        }

        public void StaleGeneration()
        {
            _server.Publish(Required());
            var old = Plan();
            _server.Publish(new ManifestMod { Id = Id + 1 }.Add(Second, "def"));
            var progress = new SyncProgress();
            Check(Execute(old, progress) == SyncResult.Replan, "stale generation did not request a new plan");
            Unchanged();
            var fresh = Plan();
            Check(fresh.Installs.Single().Id == Id + 1 && fresh.Removals.Single().Id == Id, "fresh chooser did not get changed selection");
        }

        public void RangeFaults()
        {
            SeedPartial();
            var data = Directory.GetFiles(Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2"), "*" + Abc + "-3.data").Single();
            foreach (var fault in new[] { "offset", "total", "validator", "encoding" })
            {
                _server.Fault = fault;
                Check(Run() == SyncResult.Failed, "accepted malformed range: " + fault);
                Unchanged();
                Check(File.ReadAllText(data) == "ab", "malformed range appended bytes");
                _server.ExpectObjects(Abc);
                _server.ClearRequests();
            }
            _server.Fault = "";
            Check(Run() == SyncResult.RestartRequired, "valid range failed after malformed attempts");
            _server.ExpectObjects(Abc);
        }

        public void ReplacePartial()
        {
            SeedPartial();
            _server.Fault = "full";
            var progress = new SyncProgress();
            Check(Execute(Plan(), progress) == SyncResult.RestartRequired && ReadIfPresent(First) == "abc", "full 200 was appended rather than replacing");
            _server.ExpectObjects(Abc);
            var view = progress.Snapshot();
            Check(view.Received == 3 && view.Reused == 3 && view.Planned == 6, "discarded prefix counted as completion");
        }

        public void Checkpoint(string mode)
        {
            SeedPartial();
            var data = Directory.GetFiles(Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2"), "*" + Abc + "-3.data").Single();
            var meta = Path.ChangeExtension(data, ".meta");
            if (mode == "complete" || mode == "corrupt")
            {
                File.WriteAllText(data, mode == "complete" ? "abc" : "bad");
                File.WriteAllText(meta, "\"" + Abc + "\"\n3");
            }
            else if (mode == "shortened") File.WriteAllText(data, "a");
            else if (mode == "forged") File.WriteAllText(meta, "W/\"" + Abc + "\"\n2");
            else _server.Fault = "416";
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == "def", "invalid checkpoint affected destination: " + mode);
            _server.ExpectObjects(mode == "complete" ? Array.Empty<string>() : mode == "416" ? new[] { Abc, Abc } : new[] { Abc });
            var actual = _server.Ranges.ToArray();
            Check(actual.SequenceEqual(mode == "complete" ? Array.Empty<string>() : mode == "416"
                ? new[] { "bytes=2-|\"" + Abc + "\"", "|" } : new[] { "|" }), "wrong checkpoint recovery requests: " + mode);
        }

        public void Preparing()
        {
            _server.Publish(Required());
            _server.Fault = "preparing";
            var progress = new SyncProgress();
            var run = Task.Run(() => _engine.PlanSync(progress));
            Check(SpinWait.SpinUntil(() => progress.Snapshot().Phase == "Preparing on server" || run.IsCompleted, 5000)
                && !run.IsCompleted && progress.TryCancel(), "preparing not cancellable");
            try { run.GetAwaiter().GetResult(); throw new InvalidOperationException("preparing plan succeeded after cancel"); }
            catch (OperationCanceledException) { }
            Unchanged();
            _server.ExpectObjects();
            Check(_server.CatalogRequests == 1, "cancelled preparing requested another catalog");
        }

        public void RetryAfter()
        {
            _server.Publish(Required());
            _server.FaultDigest = Abc;
            _server.Fault = "busy";
            var plan = Plan();
            var clock = Stopwatch.StartNew();
            Check(Run(plan) == SyncResult.RestartRequired, "bounded Retry-After never reached a fresh response");
            _server.ExpectObjects(Abc, Abc, Abc, Abc);
            Check(clock.Elapsed.TotalSeconds >= 14.5 && clock.Elapsed.TotalSeconds < 24, "Retry-After cap outside expected interval");
            Console.WriteLine($"busy: 4 GETs, first backoff capped; total {clock.Elapsed.TotalSeconds:F2}s");
        }

        public void FreeSpace()
        {
            _server.Publish(Required());
            _server.Edit((doc, _) => doc["groups"]![0]!["mods"]![0]!["files"]![0]!["length"] = long.MaxValue / 4, null);
            var config = new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(_root, "BepInEx/config/cap.cfg"), false)) { SpoolMaxBytes = long.MaxValue };
            var engine = new SyncEngine(new ManualLogSource("Space"), config);
            var progress = new SyncProgress();
            Check(engine.ExecuteSync(engine.PlanSync()!, new List<long> { Id }, new HashSet<long>(), Array.Empty<ReplacementConsent>(), progress) == SyncResult.Failed
                && progress.Snapshot().Error!.Contains("free space"), "volume headroom not checked");
            Unchanged();
            _server.ExpectObjects();
        }

        public void DiscardedProgress()
        {
            _server.Publish(Required().Add(Second, "abc"));
            _server.FaultDigest = Abc;
            _server.Fault = "corrupt-once";
            using var retry = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var count = 0;
            _server.ObjectRequested = () =>
            {
                if (Interlocked.Increment(ref count) == 2) { retry.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
            };
            var plan = Plan();
            var clock = typeof(SyncProgress).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Func<long>) }, null)!;
            var progress = (SyncProgress)clock.Invoke(new object[] { (Func<long>)(() => 0L) });
            var run = Task.Run(() => Execute(plan, progress));
            try
            {
                Check(retry.Wait(TimeSpan.FromSeconds(10)), "corruption did not reach second GET");
                var interim = progress.Snapshot();
                Check(interim.Planned == 3 && interim.Received == 0 && interim.Reused == 0 && interim.BytesPerSecond == 0,
                    "discarded bytes remained in completion or socket rate");
                Unchanged();
            }
            finally { release.Set(); }
            Check(run.Wait(10_000) && run.Result == SyncResult.RestartRequired, "retry did not install");
            var final = progress.Snapshot();
            Check(final.Received == 3 && final.Reused == 0 && ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == "abc",
                "unique accepted bytes double-counted retry or duplicate digest");
            _server.ExpectObjects(Abc, Abc);
        }

        public void CancelRepair(bool bundlesOnly)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            var baseline = bundlesOnly ? InstallBundleProof() : OriginalState;
            var catalog = _engine.FetchCatalogPublic();
            var progress = new SyncProgress();
            using var requested = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            _server.Conditional = () => { requested.Set(); release.Wait(TimeSpan.FromSeconds(15)); };
            var worker = Task.Run(() => _engine.ExecuteRedownload(catalog, bundlesOnly, progress));
            try { Check(requested.Wait(TimeSpan.FromSeconds(10)) && progress.TryCancel(), "could not cancel repair at publication check"); }
            finally { release.Set(); }
            Check(worker.Wait(10_000) && worker.Result == SyncResult.Cancelled && ReadIfPresent(Bundle) == null,
                "cancelled repair advanced to apply");
            Check(ReadState() == baseline, "cancelled repair changed state");
            if (!bundlesOnly) Unchanged();
            _server.ExpectObjects(bundlesOnly ? new[] { Sha("bundle bytes") } : new[] { Abc, Sha("bundle bytes") });
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int link(string source, string target);

        public void Linked(string mode)
        {
            SeedPartial();
            var spool = Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2");
            var data = mode == "lock" ? Path.Combine(spool, "root.lock") : Directory.GetFiles(spool, "*" + Abc + "-3.data").Single();
            var backup = Path.Combine(_root, "retained-backup");
            File.WriteAllText(backup, mode == "lock" ? new string('b', 32) : "ab");
            File.Delete(data);
            Check(link(backup, data) == 0, "fixture hard link failed");
            if (mode == "full") _server.Fault = "full";
            if (mode != "lock") _server.ConditionalFault = _ => "disabled";
            try { Run(); } catch (IOException) when (mode == "lock") { }
            Check(File.ReadAllText(backup) == (mode == "lock" ? new string('b', 32) : "ab"), "linked inode mutated: " + mode);
            if (mode != "lock")
            {
                Unchanged();
                _server.ExpectObjects(Abc);
                Check(_server.Ranges.ToArray().SequenceEqual(new[] { "bytes=2-|\"" + Abc + "\"" }), "linked test did not reach ranged request");
            }
            else Check(ReadIfPresent(First) == null && ReadState() == OriginalState, "linked lock admitted writes");
        }

        public void Publication(string code, bool changed)
        {
            _server.Publish(Required());
            var plan = Plan();
            _server.ConditionalFault = n =>
            {
                if (n == 1) return code;
                if (changed) _server.PublishWithoutClearing(Required().Add(Second, "def"));
                return null;
            };
            var progress = new SyncProgress();
            var expected = changed ? SyncResult.Replan : code == "disabled" || code == "publish_failed" ? SyncResult.Failed : SyncResult.RestartRequired;
            Check(Execute(plan, progress) == expected, "wrong final publication result: " + code);
            Check(_server.ConditionalCount == (expected == SyncResult.Failed ? 1 : 2), "wrong final conditional count");
            if (expected != SyncResult.RestartRequired) Unchanged();
        }

        public void ObjectPreparing(bool changed)
        {
            _server.Publish(Required());
            _server.FaultDigest = Abc;
            _server.Fault = "preparing-object";
            _server.ConditionalFault = n =>
            {
                if (n == 1) return "preparing";
                if (n == 2 && changed) _server.PublishWithoutClearing(Required().Add(Second, "def"));
                return null;
            };
            var progress = new SyncProgress();
            Check(Execute(Plan(), progress) == (changed ? SyncResult.Replan : SyncResult.RestartRequired), "object preparing did not revalidate");
            Check(_server.ConditionalCount == (changed ? 2 : 3), "wrong object revalidation count");
            _server.ExpectObjects(changed ? new[] { Abc } : new[] { Abc, Abc });
            if (changed) Unchanged();
        }

        public void ForeignRemoval()
        {
            _server.Publish();
            var plan = Plan();
            var prop = typeof(SyncEngine).GetProperty("VolumeIdentity", BindingFlags.NonPublic | BindingFlags.Instance)!;
            prop.SetValue(_engine, (Func<string, string>)(path => path == Path.Combine(_root, Old) ? "foreign" : "game"));
            var progress = new SyncProgress();
            Check(Execute(plan, progress) == SyncResult.Failed && progress.Snapshot().Error!.Contains("same volume"), "removal bypassed volume admission");
            Unchanged();
            _server.ExpectObjects();
            Check(_server.ConditionalCount == 0, "removal reached precommit publication check");
        }

        public void StaleRepair(bool bundlesOnly)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            var baseline = bundlesOnly ? InstallBundleProof() : OriginalState;
            var old = _engine.FetchCatalogPublic();
            var nextBundle = bundlesOnly ? "bundle bytes" : "changed bundle";
            _server.Publish(Required().Add(Bundle, nextBundle), Optional().Add(Second, "unselected"));
            var result = _engine.ExecuteRedownload(old, bundlesOnly, new SyncProgress());
            Check(result == SyncResult.Replan, "repair did not see stale generation");
            Check(ReadState() == baseline && ReadIfPresent(Bundle) == null, "stale repair touched files/state");
            if (!bundlesOnly) Unchanged();
            var route = typeof(ConvoyPlugin).GetMethod("OpenFreshChooserAfterReplan", BindingFlags.NonPublic | BindingFlags.Static)!;
            Check((bool)route.Invoke(null, new object?[] { null })! && !(bool)route.Invoke(null, new object[] { bundlesOnly })!, "repair was routed into ordinary chooser");
            _server.ClearRequests();
            var retry = typeof(SyncEngine).GetMethod("ExecuteFreshRepair", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var retryResult = (SyncResult)retry.Invoke(_engine, new object[] { bundlesOnly, new SyncProgress() })!;
            Check(retryResult == (bundlesOnly ? SyncResult.Applied : SyncResult.RestartRequired), "explicit fresh repair failed");
            _server.ExpectObjects(bundlesOnly ? new[] { Sha(nextBundle) } : new[] { Abc, Sha(nextBundle) });
            Check(ReadIfPresent(Bundle) == nextBundle && ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == null, "repair mode changed on retry");
        }

        private static void SetObserver(SyncProgress progress, string name, Delegate observer) =>
            typeof(SyncProgress).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(progress, observer);

        public void CancelBody()
        {
            _server.Publish(Required());
            _server.FaultDigest = Abc;
            _server.Fault = "blocked-body";
            using var release = new ManualResetEventSlim();
            using var read = new ManualResetEventSlim();
            _server.BodyPrefix = () => release.Wait(TimeSpan.FromSeconds(20));
            var progress = new SyncProgress();
            SetObserver(progress, "BeforeObjectRead", (Action<long>)(length => { if (length == 2) read.Set(); }));
            var plan = Plan();
            var work = Task.Run(() => Execute(plan, progress));
            try
            {
                Check(read.Wait(TimeSpan.FromSeconds(10)) && progress.Snapshot().Received == 2, "body prefix was not written");
                Check(progress.TryCancel() && work.Wait(5000) && work.Result == SyncResult.Cancelled,
                    "cancel did not interrupt the blocked body while the server still withheld the tail");
                Unchanged();
                _server.ExpectObjects(Abc);
                var spool = Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2");
                var data = Directory.GetFiles(spool, "*" + Abc + "-3.data").Single();
                Check(File.ReadAllText(data) == "ab" && File.ReadAllText(Path.ChangeExtension(data, ".meta")) == "\"" + Abc + "\"\n2", "cancel did not checkpoint exactly the prefix");
            }
            finally { release.Set(); }
            _server.ClearRequests();
            _server.Fault = "";
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(First) == "abc", "cancelled prefix did not resume");
            _server.ExpectObjects(Abc);
            Check(_server.Ranges.ToArray().SequenceEqual(new[] { "bytes=2-|\"" + Abc + "\"" }), "cancelled prefix resumed at wrong position");
        }

        public void CancelHash()
        {
            var oldBytes = new string('x', 180000);
            Write(Old, oldBytes);
            _state.Mods.Single().Files.Single().Hash = Hash(oldBytes);
            _state.Save();
            var before = ReadState();
            _server.Publish(Required());
            var plan = Plan();
            using var hashed = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var chunks = 0;
            var progress = new SyncProgress();
            SetObserver(progress, "LocalHashChunk", (Action<string>)(path =>
            {
                if (path == Old && Interlocked.Increment(ref chunks) == 1)
                { hashed.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
            }));
            var work = Task.Run(() => Execute(plan, progress));
            try { Check(hashed.Wait(TimeSpan.FromSeconds(10)) && progress.TryCancel(), "did not cancel at local hash chunk"); }
            finally { release.Set(); }
            Check(work.Wait(5000) && work.Result == SyncResult.Cancelled && chunks == 1, "hash cancellation lost or continued hashing after cancel");
            Check(ReadState() == before && ReadIfPresent(Old) == oldBytes && ReadIfPresent(First) == null && ReadIfPresent(Second) == null, "cancelled hashing mutated install");
            _server.ExpectObjects();
        }

        private static ManifestMod AssetBatch(string label, int count) => new ManifestMod { Id = Id, Assets = Enumerable.Range(0, count)
            .Select(i => ($"BepInEx/plugins/fixture/{label}-{i}.dll", $"{label} payload {i}")).ToList() };

        public void ConcurrentLocalReads(bool precommit, bool cancel = false)
        {
            Fresh();
            var readers = Math.Max(1, Environment.ProcessorCount - 2);
            var mod = AssetBatch("local", readers + 1);
            foreach (var asset in mod.Assets) Write(asset.Path, asset.Bytes);
            var shared = new ManifestMod { Id = Id + 1, Assets = { mod.Assets[0] } };
            _server.Publish(mod, shared);
            var plan = Plan();
            var progress = new SyncProgress();
            var phase = precommit ? "Verifying" : "Checking installed files";
            var seen = new ConcurrentQueue<string>();
            var slots = cancel ? Math.Min(2, readers) : readers;
            using var wave = new HeldWave(slots);
            SetObserver(progress, "LocalHashChunk", (Action<string>)(path =>
            {
                if (progress.Snapshot().Phase != phase) return;
                seen.Enqueue(path);
                wave.Hold();
            }));
            var work = Task.Run(() => Execute(plan, progress));
            try
            {
                wave.WaitFor(phase + " reads");
                Check(!File.Exists(StateFile) && _server.CatalogRequests == 1, "published or applied before joining local reads");
                if (cancel)
                {
                    Check(progress.TryCancel() && !work.IsCompleted, "cancellation left readers running beyond operation lifetime");
                    var locked = false;
                    try { SavePanelDelta(_engine); }
                    catch (TargetInvocationException ex) when (ex.InnerException is IOException io && io.Message.Contains("holds this game root")) { locked = true; }
                    Check(locked, "root lock not held while cancelled readers were still active");
                }
                else Check(wave.Entered == slots && seen.Distinct().Count() == slots, "wrong number of distinct local reads before release");
            }
            finally { wave.Release(); Check(work.Wait(10_000), "local readers did not join"); }
            if (cancel)
            {
                Check(work.Result == SyncResult.Cancelled && !File.Exists(StateFile) && _server.CatalogRequests == 1,
                    "cancelled scan reached publication or saved state");
                Check(Run() == SyncResult.UpToDate, "root could not be reused after cancelled readers joined");
            }
            else
            {
                Check(work.Result == SyncResult.UpToDate, "parallel observation failed: " + progress.Error);
                Check(seen.OrderBy(path => path).SequenceEqual(mod.Assets.Select(asset => asset.Path).OrderBy(path => path)),
                    "matching shared path was read more than once in the phase, or another path was missed");
                Check(progress.Snapshot().Scanned == mod.Assets.Count && progress.Snapshot().Expected == mod.Assets.Count,
                    "wrong installed-file progress");
            }
            _server.ExpectObjects();
            var installed = ConvoyState.Load().Mods;
            Check(mod.Assets.All(a => ReadIfPresent(a.Path) == a.Bytes) && installed.Count == 2
                && installed.SelectMany(m => m.Files).All(f => f.Owned == false), "matching local bytes changed or became owned");
        }

        public void ConcurrentObjects()
        {
            var mod = AssetBatch("http", 17);
            _server.Publish(mod);
            var request = WebRequest.CreateHttp(new Uri(_server.Url));
            request.Proxy = null;
            var servicePoint = request.ServicePoint;
            var originLimit = servicePoint.ConnectionLimit;
            var defaultLimit = ServicePointManager.DefaultConnectionLimit;
            bool SharedLimitsUnchanged() => servicePoint.ConnectionLimit == originLimit && ServicePointManager.DefaultConnectionLimit == defaultLimit;
            var plan = Plan();
            var progress = new SyncProgress();
            using var wave = new HeldWave(16);
            _server.Fault = "blocked-bodies";
            _server.BodyPrefix = wave.Hold;
            var work = Task.Run(() => Execute(plan, progress));
            try
            {
                wave.WaitFor("HTTP object bodies");
                Unchanged();
                Check(mod.Assets.All(a => ReadIfPresent(a.Path) == null) && _server.CatalogRequests == 1,
                    "live files or publication changed before the object bodies finished");
                Check(wave.Entered == 16 && _server.ObjectRequestCount == 16, "a seventeenth object request entered before a slot freed");
                Check(SharedLimitsUnchanged(), "Convoy changed a shared HTTP connection limit during download");
            }
            finally { wave.Release(); Check(work.Wait(15_000), "object workers did not join"); }
            Check(work.Result == SyncResult.RestartRequired && mod.Assets.All(a => ReadIfPresent(a.Path) == a.Bytes),
                "verified objects did not all install: " + progress.Error);
            Check(SharedLimitsUnchanged(), "Convoy changed a shared HTTP connection limit");
            _server.ExpectObjects(mod.Assets.Select(a => Sha(a.Bytes)).ToArray());
            Console.WriteLine($"HTTP overlap: {wave.Entered} held bodies, {mod.Assets.Count} verified objects, shared limit {originLimit}/{defaultLimit} unchanged");
        }

        public void CancelRetry()
        {
            _server.Publish(Required());
            _server.Fault = "busy";
            _server.FaultDigest = Abc;
            var plan = Plan();
            var progress = new SyncProgress();
            var work = Task.Run(() => Execute(plan, progress));
            Check(SpinWait.SpinUntil(() => progress.Snapshot().Phase == "Retrying" || work.IsCompleted, 5000)
                && !work.IsCompleted && progress.TryCancel(), "retry wait did not become cancellable");
            Check(work.Wait(5000) && work.Result == SyncResult.Cancelled, "retry wait was not interrupted");
            _server.ExpectObjects(Abc);
            Unchanged();
        }

        private static void SavePanelDelta(SyncEngine engine) => typeof(SyncEngine)
            .GetMethod("SavePanelSelection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, new object[] { new[] { "panel-option" }, Array.Empty<string>(), Array.Empty<long>(), Array.Empty<long>() });

        public void PanelContender()
        {
            _server.Publish(Required());
            using var requested = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            _server.ObjectRequested = () => { requested.Set(); release.Wait(TimeSpan.FromSeconds(25)); };
            using var holder = StartChild();
            try
            {
                Check(requested.Wait(TimeSpan.FromSeconds(10)), "holder did not reach GET");
                using var panel = StartChild(panel: true);
                Check(Finish(panel) == 13 && !holder.HasExited, "panel writer did not contend with held lock");
                Unchanged();
            }
            finally { release.Set(); }
            Check(Finish(holder) == 0, "holder failed to publish new ownership");
            var newer = ConvoyState.Load();
            var owner = newer.Mods.Single();
            Check(owner.Files.Any(f => f.Path == First), "holder did not record new path");
            using var retry = StartChild(panel: true);
            Check(Finish(retry) == 0, "panel delta retry failed");
            var updated = ConvoyState.Load();
            Check(updated.EnabledGroups.Contains("panel-option") && JToken.DeepEquals(JToken.FromObject(newer.Mods), JToken.FromObject(updated.Mods)),
                "panel retry lost newer ownership");
            _server.ExpectObjects(Abc);
        }

        public void ErrorBodyDeadline()
        {
            _server.Publish(Required());
            var plan = Plan();
            using var release = new ManualResetEventSlim();
            using var prefix = new ManualResetEventSlim();
            _server.ConditionalFault = n => n == 1 ? "blocked-error" : null;
            _server.BodyPrefix = () => { prefix.Set(); release.Wait(TimeSpan.FromSeconds(29)); };
            var clock = Stopwatch.StartNew();
            var work = Task.Run(() => Execute(plan, new SyncProgress()));
            try
            {
                Check(prefix.Wait(5000), "error body prefix was not delivered");
                Check(work.Wait(24000) && work.Result == SyncResult.RestartRequired && _server.ConditionalCount == 2,
                    "final error body did not time out and retry before fixture released it");
                Check(clock.Elapsed.TotalSeconds >= 14.5, "catalog retry bypassed the per-attempt deadline");
                Console.WriteLine($"r1 error body deadline: 2 conditional GETs in {clock.Elapsed.TotalSeconds:F2}s");
            }
            finally { release.Set(); }
        }

        public void ApplyBudget(string mode)
        {
            var second = Sha("def");
            ServicePointManager.FindServicePoint(new Uri(_server.Url)).ConnectionLimit = 8;
            _server.Publish(Required().Add(Second, "def"));
            var clock = 0L;
            var delivery = typeof(SyncEngine).GetField("_delivery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_engine)!;
            delivery.GetType().GetProperty("MonotonicMilliseconds", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(delivery, (Func<long>)(() => Interlocked.Read(ref clock)));
            using var firstRevalidated = new ManualResetEventSlim();
            var firstGets = 0;
            var secondGets = 0;
            _server.ObjectFault = digest =>
            {
                if (digest == Abc)
                {
                    if (Interlocked.Increment(ref firstGets) == 1) return "preparing";
                    if (!mode.StartsWith("transient", StringComparison.Ordinal)) Interlocked.Exchange(ref clock, 181_000);
                    firstRevalidated.Set();
                    return null;
                }
                if (digest == second && Interlocked.Increment(ref secondGets) == 1)
                {
                    if (!firstRevalidated.Wait(10_000)) throw new IOException("fixture did not observe first revalidation");
                    if (mode == "digest" || mode == "transient-digest") return "preparing";
                }
                return null;
            };
            _server.ConditionalFault = n =>
            {
                if (mode.StartsWith("transient", StringComparison.Ordinal))
                    return n <= 3 || n == 5 ? "busy" : null;
                return mode == "final" && n == 2 ? "preparing" : null;
            };
            var plan = Plan();
            var epochPath = Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2", "root.lock");
            var epoch = File.ReadAllBytes(epochPath);
            var progress = new SyncProgress();
            var result = Execute(plan, progress);
            progress.Complete(result);
            Console.WriteLine($"r2 observed {mode}: {result} {progress.Error}; {firstGets}/{secondGets} object GETs, {_server.ConditionalCount} conditional GETs");
            var expected = mode == "healthy" ? SyncResult.RestartRequired : SyncResult.Failed;
            Check(result == expected, "wrong operation budget result " + mode + ": " + result);
            if (expected == SyncResult.Failed)
            {
                var view = progress.Snapshot();
                Check(epoch.SequenceEqual(File.ReadAllBytes(epochPath)), "exhausted budget entered apply");
                Check(view.CanRetry && view.Error!.Contains(mode.StartsWith("transient", StringComparison.Ordinal) ? "busy" : "still preparing"), "budget exhaustion was not actionable");
                Unchanged();
            }
            else Check(ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == "def", "healthy final proof did not apply");
            var conditional = mode == "digest" ? 1 : mode.StartsWith("transient", StringComparison.Ordinal) ? 5 : 2;
            Check(_server.ConditionalCount == conditional && _server.CatalogRequests == conditional + 1, "manifest budget made extra requests");
            _server.ExpectObjects(Abc, Abc, second);
            Console.WriteLine($"r2 {mode}: 3 object GETs, {conditional} conditional GETs, {result}");
        }

        public void ConcurrentBudget()
        {
            ServicePointManager.FindServicePoint(new Uri(_server.Url)).ConnectionLimit = 8;
            _server.Publish(Required().Add(Second, "def"));
            _server.ObjectFault = _ => "preparing";
            using var simultaneous = new CountdownEvent(2);
            _server.ConditionalFault = n =>
            {
                if (n <= 2)
                {
                    simultaneous.Signal();
                    if (!simultaneous.Wait(10_000)) throw new IOException("two object checks did not overlap");
                }
                return "busy";
            };
            var progress = new SyncProgress();
            Check(Execute(Plan(), progress) == SyncResult.Failed && simultaneous.IsSet && progress.Error!.Contains("busy"),
                "retry exhaustion was not observed by both active workers");
            Check(_server.ConditionalCount == 5 && _server.CatalogRequests == 6,
                "two initial checks did not share exactly three retry slots: " + _server.ConditionalCount);
            _server.ExpectObjects(Abc, Sha("def"));
            Unchanged();
            Console.WriteLine("r2 concurrent: 2 overlapping initial conditionals + 3 shared retries; 2 object GETs");
        }

        public void FinalAdmission(bool volume)
        {
            _server.Publish(Required());
            var plan = Plan();
            var lockFile = Path.Combine(Paths.ConfigPath, "Convoy", "transfer-v2", "root.lock");
            var epoch = File.ReadAllBytes(lockFile);
            var probes = new System.Collections.Concurrent.ConcurrentQueue<int>();
            if (volume)
                typeof(SyncEngine).GetProperty("VolumeIdentity", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_engine,
                    (Func<string, string>)(path =>
                    {
                        if (path != Path.Combine(_root, Old)) return "game";
                        var request = Volatile.Read(ref _server.ConditionalCount);
                        probes.Enqueue(request);
                        return request >= 2 ? "foreign" : "game";
                    }));
            else
                typeof(SyncEngine).GetProperty("AvailableSpace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_engine,
                    (Func<string, long>)(_ =>
                    {
                        var request = Volatile.Read(ref _server.ConditionalCount);
                        probes.Enqueue(request);
                        return request >= 2 ? 0L : long.MaxValue;
                    }));
            _server.ConditionalFault = n => n == 1 ? "preparing" : null;
            var progress = new SyncProgress();
            var result = Execute(plan, progress);
            Check(result == SyncResult.Failed && progress.Snapshot().Error!.Contains(volume ? "same volume" : "free space"), "renewed disk admission did not reject before apply: " + result);
            Check(probes.Contains(0) && probes.Contains(2), "disk probe did not cross the real publication fence");
            Check(epoch.SequenceEqual(File.ReadAllBytes(lockFile)) && progress.Snapshot().Phase != "Applying", "disk rejection entered apply");
            Check(_server.ConditionalCount == 2 && _server.CatalogRequests == 3, "wrong final publication requests");
            Unchanged();
            _server.ExpectObjects(Abc);
        }

        public void Install(bool firstExists, bool secondExists)
        {
            Fresh();
            if (firstExists) Write(First, "abc");
            if (secondExists) Write(Second, "abc");
            Write(Config, "personal settings");
            var otherBytes = firstExists && !secondExists ? "def" : "abc";
            _server.Publish(Required().Add(Config, "defaults"), new ManifestMod { Id = Id + 1 }.Add(Second, otherBytes));
            var result = Run();
            Check(result == (secondExists ? SyncResult.UpToDate : SyncResult.RestartRequired), "wrong install result: " + result);
            Check(ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == otherBytes && ReadIfPresent(Config) == "personal settings", "wrong delivered bytes");
            _server.ExpectObjects(secondExists ? Array.Empty<string>() : new[] { Sha(otherBytes) });
            var state = ConvoyState.Load();
            Check(state.Mods.Select(m => m.Id).OrderBy(x => x).SequenceEqual(new[] { Id, Id + 1 }), "64-bit mod IDs changed");
            var files = state.Mods.SelectMany(m => m.Files).ToDictionary(f => f.Path);
            Check(files[First].Owned == !firstExists && files[Second].Owned == !secondExists, "wrong ownership for matching paths");
            Check(files[First].Sha256 == Abc && files[Second].Length == 3 && files[Config].Sha256 == null && files[Config].Owned == false, "incorrect proof for installed or preserved files");
        }

        public void NoOp()
        {
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired, "initial install failed");
            _server.ClearRequests();
            // Preserve existing behavior: the normal startup check uses the revision and file presence, not full payload reads.
            using var locked = new FileStream(Path.Combine(_root, First), FileMode.Open, FileAccess.Read, FileShare.None);
            var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(StateFile, oldTime);
            var state = ReadState();
            var plan = Plan();
            Check(File.GetLastWriteTimeUtc(StateFile) == oldTime && ReadState() == state, "ordinary no-op replaced state");
            Check(plan.Installs.Count + plan.Updates.Count + plan.Removals.Count == 0, "untouched pack was not a no-op");
            _server.ExpectObjects();
        }

        public void StaleState(bool missing)
        {
            if (missing) Fresh();
            _server.Publish(Required());
            var plan = Plan();
            var edited = new ConvoyState { ServerUrl = "http://127.0.0.1:1", Mods = { new ModState { Id = Id, Version = "edited" } } };
            edited.Save();
            var bytes = ReadState();
            Check(Run(plan) == SyncResult.Failed && ReadState() == bytes && ReadIfPresent(First) == null
                && ReadIfPresent(Old) == (missing ? null : "installed old file"), "stale plan used superseded state authority");
        }

        public void ConditionalState(bool repair)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            var edited = (repair ? InstallBundleProof() : OriginalState) + "\n";
            _server.Conditional = () => File.WriteAllText(StateFile, edited);
            var outcome = repair ? _engine.ExecuteRedownload(_engine.FetchCatalogPublic(), true) : Run();
            Check(outcome == SyncResult.Failed && ReadState() == edited && ReadIfPresent(First) == (repair ? "abc" : null)
                && ReadIfPresent(Bundle) == null && ReadIfPresent(Old) == (repair ? null : "installed old file"),
                "state edited during conditional request was not preserved");
        }

        public void Bookkeeping()
        {
            Fresh();
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired, "initial install failed");
            _server.Publish(Required(), Optional().Add(Second, "abc"));
            var edited = ReadState() + "\n";
            _server.Conditional = () => File.WriteAllText(StateFile, edited);
            RejectedPlan();
            Check(ReadState() == edited && ReadIfPresent(Second) == null, "bookkeeping overwrote edited state");
            _server.Conditional = null;
            Check(Plan().Updates.Count == 0 && ConvoyState.Load().OptionalGroups.Single().Slug == "optional",
                "changed optional-group bookkeeping was not saved");
            _server.ExpectObjects();
        }

        public void LocalValidationOrder()
        {
            Fresh();
            Write(First, "abc");
            _server.Publish(Required().Add(Second, "def"));
            _server.ObjectRequested = () => Write(First, "changed before final validation");
            var conditional = false;
            _server.Conditional = () => conditional = true;
            Check(Run() == SyncResult.Failed && !conditional && !File.Exists(StateFile) && ReadIfPresent(Second) == null,
                "conditional request preceded failed local verification");
        }

        public void MandatoryDirectory()
        {
            Fresh();
            Directory.Delete(Paths.ConfigPath, true);
            _server.Publish(Required(), Optional().Add("BepInEx/config", "not a directory"));
            RejectedPlan();
            Check(!Directory.Exists(Paths.ConfigPath) && ReadIfPresent(First) == null, "mandatory parent was accepted");
        }

        public void LargeDelivery()
        {
            Fresh();
            _server.PublishLarge();
            Check(Run() == SyncResult.RestartRequired, "large verified object was not installed");
            var path = Path.Combine(_root, First);
            Check(new FileInfo(path).Length == LargeLength, "large object length changed");
            using var sha = SHA256.Create();
            using var file = File.OpenRead(path);
            Check(Hex(sha.ComputeHash(file)) == LargeDigest, "large object checksum changed");
            _server.ExpectObjects(LargeDigest);
        }

        public void BadManifest(Action<ManifestMod> damage) => InvalidCatalog((doc, _) =>
        {
            var bad = Optional().Add(Second, "abc");
            damage(bad);
            doc["groups"]![1]!["mods"]![0] = DownloadFixture.ModJson(bad);
        });

        public void InvalidCatalog(Action<JObject, string>? change, Func<string, string>? json = null)
        {
            Fresh();
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired, "initial install failed");
            var state = ReadState();
            _server.Publish(Required(), Optional().Add(Second, "abc"));
            _server.Edit(change, json);
            RejectedPlan();
            Check(ReadIfPresent(First) == "abc" && ReadState() == state, "bad optional manifest changed existing install");
            _server.ExpectObjects();
        }

        private void RejectedPlan()
        {
            try { Plan(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is InvalidDataException || ex is JsonException) { return; }
            throw new InvalidOperationException("invalid plan was accepted");
        }

        public void WrongOrigin(string server)
        {
            _state.ServerUrl = server;
            _state.Save();
            var state = ReadState();
            _server.Publish(Required());
            RejectedPlan();
            Check(ReadState() == state && ReadIfPresent(Old) == "installed old file", "foreign ownership record changed");
            _server.ExpectObjects();
        }

        public void Retire()
        {
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(Old) == null && ReadIfPresent(First) == "abc"
                && ReadIfPresent(Unrelated) == "locally owned", "retirement did not preserve unrelated bytes");
            _server.ExpectObjects(Abc);
        }

        public void BadObject(string fault)
        {
            _server.Publish(Required().Add(Second, "def"));
            _server.Fault = fault;
            _server.FaultDigest = Sha("def");
            Check(Run() == SyncResult.Failed && ReadIfPresent(Old) == "installed old file" && ReadIfPresent(First) == null
                && ReadIfPresent(Second) == null && ReadState() == OriginalState, "failed batch changed live files or state");
            _server.ExpectObjects(new[] { Abc }.Concat(Enumerable.Repeat(Sha("def"), fault == "corrupt" || fault == "truncated" ? 4 : 1)).ToArray());
        }

        public void ChangedBeforeApply()
        {
            Fresh();
            Write(First, "abc");
            _server.Publish(Required());
            _server.Conditional = () => Write(First, "def");
            Check(Run() == SyncResult.Failed && !File.Exists(StateFile) && ReadIfPresent(First) == "def", "mutated local file counted as proof");
            _server.ExpectObjects();
        }

        public void RollbackRegression(bool transition, bool recoveryFailure)
        {
            var type = typeof(SyncEngine).Assembly.GetType("Convoy.SyncFiles", true)!;
            var transaction = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { _root, _state.Mods }, null)!;
            object? Call(string method, params object[] args) => type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transaction, args);
            var incoming = transition ? Old + "/child.dll" : "BepInEx/plugins/new-directory/nested/new.dll";
            var staging = Path.Combine(_root, "staging");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, incoming))!);
            File.WriteAllText(Path.Combine(staging, incoming), "verified first");
            Write(Second, "second original");
            var removed = _state.Mods.SelectMany(m => m.Files).Concat(new[] { new ModFileState { Path = Second, Hash = Hash("second original"), Owned = true } }).ToArray();
            Exception? failure = null;
            var saveCalled = false;
            try
            {
                Call("Preflight", new Dictionary<string, DeliveryAsset> { [incoming] = MakeAsset(incoming, "verified first") }, Array.Empty<ModFileState>(), removed);
                Call("Apply", (Action<string, Stream>)((path, output) => {
                    using var source = File.OpenRead(Path.Combine(staging, path));
                    source.CopyTo(output);
                }), (Action)(() => {
                    saveCalled = true;
                    if (recoveryFailure) Directory.CreateDirectory(Path.Combine(_root, Second));
                    throw new IOException("injected state save failure");
                }));
            }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
            Check(failure != null, "operation must fail");
            Check(ReadIfPresent(Old) == "installed old file" && !Directory.Exists(Path.Combine(_root, Old)), "owned file stranded after rollback");
            Check(ReadState() == OriginalState && ReadIfPresent(incoming) == null, "failed state commit mutated files/state");
            if (transition) Check(!saveCalled, "incompatible path shape must fail before mutations");
            else if (recoveryFailure)
            {
                Check(failure is AggregateException aggregate && aggregate.InnerExceptions.Count >= 2
                    && aggregate.InnerExceptions[0].Message.Contains("state save"), "original and recovery failures must both be reported");
                Check(Directory.GetFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Length == 1, "only failed recovery backup must remain");
            }
            else Check(saveCalled && !Directory.Exists(Path.Combine(_root, "BepInEx/plugins/new-directory")) && ReadIfPresent(Second) == "second original"
                && Directory.GetFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Length == 0, "successful rollback must restore all backups");
        }


        public void ManagedLifecycle(bool preexisting, bool modified = false)
        {
            if (preexisting) Write(Managed, "verified first");
            _server.Publish(Required().Add(Managed, "verified first"));
            Check(Run() == SyncResult.RestartRequired, "dependency install failed");
            Check(ConvoyState.Load().Mods.Single().Files.Single(f => f.Path == Managed).Owned == !preexisting, "wrong dependency ownership");
            _server.ExpectObjects(preexisting ? new[] { Abc } : new[] { Abc, Sha("verified first") });
            if (modified) Write(Managed, "local edit");
            _server.Publish();
            Check(Run() == SyncResult.RestartRequired, "dependency removal failed");
            Check(ReadIfPresent(Managed) == (modified ? "local edit" : preexisting ? "verified first" : null), "dependency ownership removal mismatch");
            _server.ExpectObjects();
        }

        public void Conflict(string path)
        {
            Write(path, "locally owned bytes");
            _server.Publish(new ManifestMod { Id = Id }.Add(path, "pack bytes"));
            Check(Run() == SyncResult.Failed && ReadIfPresent(path) == "locally owned bytes" && ReadState() == OriginalState, "unowned path overwritten");
            _server.ExpectObjects();
        }

        public void SharedDependency(bool decline = false)
        {
            _server.Publish(Required().Add(Managed, "old dependency"), Optional().Add(Managed, "old dependency"));
            Check(Run() == SyncResult.RestartRequired, "initial shared install failed");
            var before = ReadState();
            if (decline)
            {
                _server.Publish(Required().Add(Managed, "new dependency"), Optional().Add(Managed, "new dependency"));
                Check(Run(Plan(), Id + 1) == SyncResult.Failed && ReadState() == before, "declined revision was not protected");
            }
            else
            {
                _server.Publish(Optional().Add(Managed, "old dependency"));
                Check(Run() == SyncResult.RestartRequired && ReadIfPresent(First) == null, "failed to remove only retired mod");
            }
            Check(ReadIfPresent(Managed) == "old dependency", "shared dependency changed");
            _server.ExpectObjects();
        }

        public void DependencyUpdate(bool modified)
        {
            _server.Publish(Required().Add(Managed, "original assembly"));
            Check(Run() == SyncResult.RestartRequired, "initial dependency install failed");
            if (modified) Write(Managed, "personal assembly");
            var before = ReadState();
            _server.Publish(Required().Add(Managed, "updated dependency"));
            Check(Run() == (modified ? SyncResult.Failed : SyncResult.RestartRequired), "wrong update result");
            Check(ReadIfPresent(Managed) == (modified ? "personal assembly" : "updated dependency"), "wrong updated bytes");
            if (modified) Check(ReadState() == before, "conflicting update saved state");
            _server.ExpectObjects(modified ? Array.Empty<string>() : new[] { Sha("updated dependency") });
        }

        public void PreserveConfiguration()
        {
            Write(Config, "personal settings");
            _server.Publish(Required().Add(Config, "defaults"));
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(Config) == "personal settings", "config overwritten");
            _server.ExpectObjects(Abc);
            Check(Plan().Updates.Count == 0, "protected config caused spurious update");
            _server.Publish();
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(Config) == "personal settings", "config deleted");
        }

        public void RejectPaths()
        {
            var paths = new[] { "../escape.dll", "/absolute.dll", "C:/drive.dll", "BepInEx/../escape.dll", "BepInEx//alias.dll", "BepInEx/CON.dll", "BepInEx/plugins/a.dll.", "EscapeFromTarkov_Data/Managed/a.txt", "EscapeFromTarkov_Data/Managed/sub/a.dll", "SPT_Runtime/user/profiles/private.json", "EscapeFromTarkov_Data\\Managed\\a.dll", "BepInEx/config/Convoy/state.json" };
            foreach (var path in paths)
            {
                _server.Publish(Required().Add(path, "payload"));
                RejectedPlan();
                Check(ReadState() == OriginalState && ReadIfPresent(Old) == "installed old file", "unsafe path mutated game: " + path);
                _server.ExpectObjects();
            }
        }

        public void LinkedGameAncestor()
        {
            var physical = Path.Combine(_root, "physical");
            var actualGame = Path.Combine(physical, "game");
            var alias = Path.Combine(_root, "alias");
            Directory.CreateDirectory(actualGame);
            using var process = Process.Start(new ProcessStartInfo("ln", "-s \"" + physical + "\" \"" + alias + "\"") { UseShellExecute = false });
            process!.WaitForExit();
            Check(process.ExitCode == 0, "ancestor link fixture failed");
            try
            {
                var game = Path.Combine(alias, "game");
                Check((new DirectoryInfo(alias).Attributes & FileAttributes.ReparsePoint) != 0, "fixture ancestor is not linked");
                SetBepInExPath(nameof(Paths.GameRootPath), game);
                SetBepInExPath(nameof(Paths.ConfigPath), Path.Combine(game, "BepInEx", "config"));
                SetBepInExPath(nameof(Paths.BepInExConfigPath), Path.Combine(game, "BepInEx", "config", "BepInEx.cfg"));
                var engine = new SyncEngine(new ManualLogSource("Linked ancestor regression"),
                    new ConvoyConfig(new BepInEx.Configuration.ConfigFile(Path.Combine(game, "BepInEx/config/test.cfg"), false)));
                _server.Publish(Required());
                var plan = engine.PlanSync()!;
                Check(engine.ExecuteSync(plan, new List<long> { Id }, new HashSet<long>(), Array.Empty<ReplacementConsent>()) == SyncResult.RestartRequired,
                    "sync through linked ancestor failed");
                Check(File.ReadAllText(Path.Combine(actualGame, First)) == "abc", "linked ancestor sync installed wrong bytes");
                Check(ConvoyState.Load().Mods.Single().Files.Single().Path == First, "linked ancestor sync did not save state");
                _server.ExpectObjects(Abc);
                _server.ClearRequests();
                plan = engine.PlanSync()!;
                Check(engine.ExecuteSync(plan, new List<long>(), new HashSet<long>(), Array.Empty<ReplacementConsent>()) == SyncResult.UpToDate,
                    "linked ancestor follow-up was not a no-op");
                _server.ExpectObjects();
            }
            finally { Directory.Delete(alias); }
        }

        public void RejectLink()
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.Combine(_root, "EscapeFromTarkov_Data"));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln", "-s " + outside + " " + Path.Combine(_root, "EscapeFromTarkov_Data/Managed")) { UseShellExecute = false });
            process!.WaitForExit();
            Check(process.ExitCode == 0, "link fixture failed");
            _server.Publish(Required().Add(Managed, "abc"));
            RejectedPlan();
            Check(ReadState() == OriginalState && !File.Exists(Path.Combine(outside, "Unity.VectorGraphics.dll")), "link escaped");
            _server.ExpectObjects();
        }

        public void SameVersionUpdate()
        {
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired, "first revision failed");
            _server.Publish(Required().Add(Managed, "abc"));
            Check(Plan().Updates.Single().Id == Id, "same-version asset was ignored");
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private string RecoveryDir => Path.Combine(Path.GetDirectoryName(StateFile)!, "recovery");
        private string[] RecoveryCopies() => Directory.Exists(RecoveryDir) ? Directory.GetFiles(RecoveryDir, "*.dll") : Array.Empty<string>();
        private const string NewDirectoryAsset = "BepInEx/plugins/new-directory/nested/new.dll";

        public void Recover(string mode)
        {
            Fresh();
            Write(First, "abc");
            _server.Publish(Required());
            Check(Run() == SyncResult.UpToDate, "setup file was not reused");
            Check(ConvoyState.Load().Mods.Single().Files.Single().Owned == false, "reuse adopted ownership");
            _server.ExpectObjects();
            var mod = new ManifestMod { Id = Id, Version = "replacement" }.Add(First, "new bytes");
            var stateFailure = mode == "state-io" || mode == "rollback-io";
            if (stateFailure) mod.Add(NewDirectoryAsset, "new DLL");
            _server.Publish(mod);
            var plan = Plan();
            var previous = ReadState();
            var exactly = plan.Replacements.Single();
            var grant = new ReplacementConsent(mode == "origin" ? "http://127.0.0.1:1" : exactly.Origin,
                mode == "generation" ? Sha("old generation") : exactly.Generation, mode == "mod" ? Id + 1 : Id,
                mode == "path" ? First.ToUpperInvariant() : First, mode == "prior" ? Sha("wrong prior") : exactly.PriorSha256,
                mode == "next" ? Sha("wrong next") : exactly.NextSha256);
            var progress = new SyncProgress();
            using var cancel = new CancellationTokenSource();
            var cancelled = mode.StartsWith("cancel", StringComparison.Ordinal);
            if (mode == "edited") Write(First, "edited");
            if (mode == "now-next") Write(First, "new bytes");
            if (mode == "missing") File.Delete(Path.Combine(_root, First));
            if (mode == "final-edit" || cancelled)
                _server.Conditional = () =>
                {
                    if (mode == "final-edit" && RecoveryCopies().Length == 1) Write(First, "edited");
                    if (cancelled && RecoveryCopies().Length == 0) cancel.Cancel();
                };
            if (mode == "backup-io")
            {
                Directory.CreateDirectory(RecoveryDir);
                Check(chmod(RecoveryDir, 320) == 0, "could not make backup destination read-only"); // 0500
            }
            var stateDir = Path.GetDirectoryName(StateFile)!;
            var saveReached = false;
            if (stateFailure) SetObserver(progress, "BeforeStateSave", (Action)(() =>
            {
                saveReached = ReadIfPresent(First) == "new bytes" && ReadIfPresent(NewDirectoryAsset) == "new DLL"
                    && RecoveryCopies().Length == 1;
                Check(!progress.TryCancel(), "cancel was accepted during live apply");
                if (mode == "rollback-io")
                {
                    File.Delete(Path.Combine(_root, First));
                    Directory.CreateDirectory(Path.Combine(_root, First));
                }
                Check(chmod(stateDir, 320) == 0, "could not make state destination read-only");
            }));
            SyncResult result;
            try
            {
                result = mode == "declined" ? Run(plan) : _engine.ExecuteSync(plan, new List<long> { Id }, new HashSet<long>(),
                    new[] { grant }, mode == "cancel" ? null : progress, cancel.Token);
            }
            finally
            {
                if (mode == "backup-io") chmod(RecoveryDir, 448); // 0700
                if (stateFailure) chmod(stateDir, 448);
            }
            var copies = RecoveryCopies();
            if (mode == "approved")
            {
                Check(result == SyncResult.RestartRequired, "approved replacement never applied: " + progress.Error);
                Check(copies.Length == 1 && File.ReadAllText(copies[0]) == "abc" && ReadIfPresent(First) == "new bytes"
                    && progress.Snapshot().Recovery!.Contains(copies[0]), "wrong durable original or replacement");
                Check(ConvoyState.Load().Mods.Single().Files.Single().Owned == false, "replacement adopted ownership");
                _server.ExpectObjects(Sha("new bytes"));
                _server.Publish();
                Check(Run() == SyncResult.UpToDate && ReadIfPresent(First) == "new bytes" && File.ReadAllText(copies[0]) == "abc", "uninstall removed an unowned replacement or recovery copy");
                return;
            }
            var retained = stateFailure || mode == "final-edit";
            Check(result == (cancelled ? SyncResult.Cancelled : SyncResult.Failed) && ReadState() == previous
                && copies.Length == (retained ? 1 : 0), "unapproved/failed replacement changed state or made wrong backup: " + progress.Error);
            var expect = mode == "edited" || mode == "final-edit" ? "edited" : mode == "now-next" ? "new bytes" : mode == "missing" || mode == "rollback-io" ? null : "abc";
            Check(ReadIfPresent(First) == expect, "unapproved/failed replacement changed preimage");
            if (retained) Check(File.ReadAllText(copies[0]) == "abc" && progress.Error!.Contains(copies[0]), "original not retained or location absent");
            if (stateFailure) Check(saveReached && !Directory.Exists(Path.Combine(_root, "BepInEx/plugins/new-directory")), "real mutation/state failure did not roll back the new directory tree");
            if (mode == "rollback-io")
            {
                var rollbackCopy = Directory.GetFiles(Path.GetDirectoryName(Path.Combine(_root, First))!, "*.convoy-old-*").Single();
                Check(File.ReadAllText(rollbackCopy) == "abc" && progress.Error!.Contains(rollbackCopy) && progress.Error.Contains("Could not restore"), "actual rollback failure or recovery path not reported");
            }
            if (mode == "backup-io") Check(progress.Error!.Contains("Could not back up"), "real backup I/O failure not surfaced");
            _server.ExpectObjects(stateFailure ? new[] { Sha("new bytes"), Sha("new DLL") } : retained || cancelled || mode == "backup-io" ? new[] { Sha("new bytes") } : Array.Empty<string>());
        }

        public void ForbiddenRecovery(string mode)
        {
            Fresh();
            var path = mode == "managed" ? Managed : mode == "non-dll" ? "BepInEx/plugins/fixture/data.txt" : mode == "config" ? Config : mode == "bundle-repair" ? Bundle : First;
            Write(path, "old bytes");
            var oldOwner = mode == "another-mod" ? Id + 1 : Id;
            _server.Publish(new ManifestMod { Id = oldOwner }.Add(path, "old bytes"));
            Check(Run() == SyncResult.UpToDate, "setup bytes not reused");
            if (mode == "legacy" || mode == "missing-record")
            {
                var saved = ConvoyState.Load();
                var files = saved.Mods.Single().Files;
                if (mode == "legacy") { files[0].Sha256 = null; files[0].Length = null; }
                else files.Clear();
                saved.Save();
            }
            if (mode == "shared-retained" || mode == "forged-removal")
            {
                var saved = ConvoyState.Load();
                var copy = saved.Mods.Single();
                saved.Mods.Add(new ModState { Id = Id + 1, Version = copy.Version, Files = copy.Files, Manifest = copy.Manifest });
                saved.Save();
            }
            if (mode == "bundle-repair") Write(path, "edited");
            var prior = ReadState();
            _server.Publish(new ManifestMod { Id = Id }.Add(path, mode == "bundle-repair" ? "old bytes" : "new bytes"),
                new ManifestMod { Id = Id + 1, Optional = mode != "shared-retained" && mode != "forged-removal" }.Add(path, mode == "bundle-repair" ? "old bytes" : "new bytes"));
            if (mode == "bundle-repair") _server.Publish(new ManifestMod { Id = Id }.Add(path, "old bytes"));
            var plan = Plan();
            var grant = new ReplacementConsent(plan.ServerUrl, plan.Catalog.Generation, Id, path, Sha("old bytes"), Sha("new bytes"));
            if (mode == "forged-removal") plan.Removals.Add(new PlannedMod { Id = Id + 1 });
            if (mode != "shared-retained" && mode != "forged-removal" && mode != "full-repair") Check(plan.Replacements.Count == 0, "forbidden authority offered in plan");
            var result = mode.EndsWith("repair", StringComparison.Ordinal)
                ? _engine.ExecuteRedownload(plan.Catalog, mode == "bundle-repair")
                : _engine.ExecuteSync(plan, new List<long> { Id }, new HashSet<long>(), new[] { grant });
            Check(result == SyncResult.Failed && ReadState() == prior && RecoveryCopies().Length == 0
                && ReadIfPresent(path) == (mode == "bundle-repair" ? "edited" : "old bytes"), "forbidden recovery changed files/state");
            _server.ExpectObjects();
        }

        public void BundleProof(string mode)
        {
            Write(Config, "local config");
            var one = Required().Add(Bundle, "bundle bytes");
            var two = new ManifestMod { Id = Id + 1 }.Add(Second, "second bytes").Add(Config, "published config");
            _server.Publish(one, two);
            Check(Run() == SyncResult.RestartRequired, "initial exact proof not recorded");
            var state = ConvoyState.Load();
            Check(state.Mods.Single(m => m.Id == Id + 1).Files.Single(f => f.Path == Config).Sha256 == null, "protected config claimed local proof");
            if (mode == "missing-record" || mode == "legacy")
            {
                var installed = state.Mods.Single(m => m.Id == Id + 1);
                if (mode == "legacy") installed.Manifest = null;
                else installed.Files.RemoveAll(f => f.Path == Second);
                state.Save();
            }
            var before = ReadState();
            if (mode == "client") two.Assets[0] = (Second, "changed client bytes");
            if (mode == "bundle") one.Assets[1] = (Bundle, "changed bundle bytes");
            if (mode == "config") two.Assets[1] = (Config, "changed config bytes");
            if (mode == "missing-mod") _server.Publish(one); else _server.Publish(one, two);
            File.Delete(Path.Combine(_root, Bundle));
            var progress = new SyncProgress();
            Check(_engine.ExecuteRedownload(_engine.FetchCatalogPublic(), true, progress) == SyncResult.Failed
                && progress.Error!.Contains("normal sync first"), "incomplete/mixed revision passed proof");
            Check(ReadState() == before && ReadIfPresent(Bundle) == null && ReadIfPresent(Second) == "second bytes" && ReadIfPresent(Config) == "local config", "bundle repair wrote against mixed revision");
            _server.ExpectObjects();
        }

        public void ForceUnowned(bool bundlesOnly)
        {
            Fresh();
            Write(First, "abc");
            Write(Bundle, "bundle bytes");
            Write(Config, "local config");
            _server.Publish(Required().Add(Bundle, "bundle bytes").Add(Config, "published config"));
            Check(Run() == SyncResult.UpToDate, "could not reuse setup bytes");
            var prior = ReadState();
            var bundleTime = File.GetLastWriteTimeUtc(Path.Combine(_root, Bundle));
            _server.Publish(Required().Add(Bundle, "bundle bytes").Add(Config, "published config"), Optional().Add(Second, "unselected"));
            Check(_engine.ExecuteRedownload(_engine.FetchCatalogPublic(), bundlesOnly) == SyncResult.UpToDate, "matching unowned repair was not a no-op");
            _server.ExpectObjects(bundlesOnly ? new[] { Sha("bundle bytes") } : new[] { Abc, Sha("bundle bytes") });
            Check(ReadState() == prior && File.GetLastWriteTimeUtc(Path.Combine(_root, Bundle)) == bundleTime
                && ReadIfPresent(Second) == null && ReadIfPresent(Config) == "local config"
                && ConvoyState.Load().Mods.Single().Files.All(f => f.Owned == false), "repair changed unowned state/files or selection");
        }

        public void RecoveryAdmission(string mode)
        {
            Fresh();
            var bytes = new string('a', 256 * 1024);
            Write(First, bytes);
            _server.Publish(new ManifestMod { Id = Id }.Add(First, bytes));
            Check(Run() == SyncResult.UpToDate, "preexisting file did not become known v2 original");
            var before = ReadState();
            _server.Publish(new ManifestMod { Id = Id }.Add(First, "new bytes"));
            var plan = Plan();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var free = long.MaxValue;
            if (mode == "volume") typeof(SyncEngine).GetProperty("VolumeIdentity", flags)!.SetValue(_engine,
                (Func<string, string>)(target => target.StartsWith(RecoveryDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "other" : "game"));
            if (mode == "original-bytes") free = 128 * 1024;
            if (mode == "final-304") _server.Conditional = () => { if (RecoveryCopies().Length == 1) Interlocked.Exchange(ref free, 0); };
            typeof(SyncEngine).GetProperty("AvailableSpace", flags)!.SetValue(_engine, (Func<string, long>)(_ => Interlocked.Read(ref free)));
            var progress = new SyncProgress();
            Check(_engine.ExecuteSync(plan, new List<long> { Id }, new HashSet<long>(), plan.Replacements, progress) == SyncResult.Failed
                && progress.Error!.Contains(mode == "volume" ? "same volume" : "free space"), "backup was omitted from admission");
            Check(ReadState() == before && ReadIfPresent(First) == bytes, "recovery admission mutated live bytes/state");
            var copies = RecoveryCopies();
            Check(copies.Length == (mode == "final-304" ? 1 : 0), "admission made incorrect recovery copies");
            if (copies.Length == 1) Check(File.ReadAllText(copies[0]) == bytes, "verified original was not retained after failed admission");
            _server.ExpectObjects(mode == "volume" ? Array.Empty<string>() : new[] { Sha("new bytes") });
        }

        public void RepairResume()
        {
            SeedPartial();
            _server.Publish(Required());
            Check(_engine.ExecuteRedownload(_engine.FetchCatalogPublic(), false) == SyncResult.RestartRequired && ReadIfPresent(First) == "abc", "repair did not install from retained prefix");
            _server.ExpectObjects(Abc);
            Check(_server.Ranges.ToArray().SequenceEqual(new[] { "bytes=2-|\"" + Abc + "\"" }), "repair did not resume exact partial");
            _server.Publish(Required().Add(Second, "def"));
            Check(Run() == SyncResult.RestartRequired && ReadIfPresent(Second) == "def", "unrelated staging was discarded");
            _server.ExpectObjects();
        }

        public void ExternalRepairCancel(bool bundlesOnly, bool withProgress)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            var baseline = bundlesOnly ? InstallBundleProof() : OriginalState;
            using var cancel = new CancellationTokenSource();
            var catalog = _engine.FetchCatalogPublic();
            _server.Conditional = cancel.Cancel;
            Check(_engine.ExecuteRedownload(catalog, bundlesOnly, withProgress ? new SyncProgress() : null, cancel.Token) == SyncResult.Cancelled,
                "external token did not cancel repair at publication");
            Check(ReadState() == baseline && ReadIfPresent(Bundle) == null, "cancelled repair wrote files/state");
            _server.ExpectObjects(bundlesOnly ? new[] { Sha("bundle bytes") } : new[] { Abc, Sha("bundle bytes") });
        }

        public void SwappedPlan(bool origin)
        {
            if (origin)
            {
                Fresh();
                _server.Publish(Required());
                var captured = Plan();
                using var foreign = new DownloadFixture();
                foreign.Publish(new ManifestMod { Id = Id, Version = "foreign" }.Add(First, "foreign bytes"));
                try
                {
                    Host(foreign.Url);
                    captured.ServerUrl = foreign.Url;
                    captured.Catalog = _engine.FetchCatalogPublic();
                    Check(Run(captured) == SyncResult.Failed && !File.Exists(StateFile) && ReadIfPresent(First) == null, "substituted origin applied");
                    foreign.ExpectObjects();
                }
                finally { Host(_server.Url); }
            }
            else
            {
                _server.Publish(Required());
                Check(Run() == SyncResult.RestartRequired, "owned fixture install failed");
                var state = ReadState();
                _server.Publish(new ManifestMod { Id = Id, Version = "confirmed" }.Add(First, "confirmed"));
                var plan = Plan();
                _server.Publish(new ManifestMod { Id = Id, Version = "unconfirmed" }.Add(First, "unconfirmed"));
                plan.Catalog = _engine.FetchCatalogPublic();
                Check(Run(plan) == SyncResult.Failed && ReadState() == state && ReadIfPresent(First) == "abc", "swapped real generation applied");
            }
            _server.ExpectObjects();
            Check(RecoveryCopies().Length == 0, "rejecting a plan made a recovery copy");
        }

        public void Appeared(bool config)
        {
            Fresh();
            var path = config ? Config : First;
            _server.Publish(new ManifestMod { Id = Id }.Add(path, "pack bytes"));
            var plan = Plan();
            var appeared = false;
            var progress = new SyncProgress();
            SetObserver(progress, "BeforeLocalObservation", (Action<string>)(p =>
            {
                if (p != path) return;
                Check(ReadIfPresent(p) == null, "appearance fixture was not absent");
                Write(p, "user bytes");
                appeared = true;
            }));
            var result = Execute(plan, progress);
            Check(appeared && result == (config ? SyncResult.UpToDate : SyncResult.Failed)
                && ReadIfPresent(path) == "user bytes", "authority derived from stale absence");
            if (config)
            {
                var record = ConvoyState.Load().Mods.Single().Files.Single();
                Check(record.Owned == false && record.Sha256 == null && record.Hash == "", "protected config acquired proof or ownership");
            }
            else Check(!File.Exists(StateFile), "unowned DLL advanced state");
            _server.ExpectObjects();
        }

        private static long ProcessBytesWritten() => long.Parse(File.ReadLines("/proc/self/io")
            .First(line => line.StartsWith("wchar:", StringComparison.Ordinal)).Substring(6));

        public void PreparedInstall(bool removeCache)
        {
            Fresh();
            _server.Publish(Required());
            var initial = new SyncProgress();
            Check(Execute(Plan(), initial) == SyncResult.RestartRequired, "owned fixture install failed: " + initial.Error);
            var before = ReadState();
            var body = new string('a', 256 * 1024);
            var digest = Sha(body);
            _server.Publish(new ManifestMod { Id = Id, Version = "prepared" }.Add(First, body).Add(Second, body));
            long atFence = -1, afterPlacement = -1;
            var reachedFence = false;
            _server.Conditional = () =>
            {
                var cache = Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "*-" + digest + "-*.data", SearchOption.AllDirectories).Single();
                Check(new FileInfo(cache).Length == body.Length && ReadIfPresent(First) == "abc" && ReadIfPresent(Second) == null,
                    "fixture did not reach the final fence with complete staged bytes and intact live files");
                if (removeCache) File.Delete(cache);
                else atFence = ProcessBytesWritten();
                reachedFence = true;
            };
            var progress = new SyncProgress();
            if (!removeCache)
                SetObserver(progress, "BeforeStateSave", (Action)(() => afterPlacement = ProcessBytesWritten()));
            var result = Execute(Plan(), progress);
            Check(reachedFence && result == SyncResult.RestartRequired, "prepared placement failed: " + progress.Error);
            Check(ReadIfPresent(First) == body && ReadIfPresent(Second) == body && ReadState() != before,
                "prepared placement did not install both files and commit state");
            if (!removeCache)
            {
                Check(atFence >= 0 && afterPlacement >= atFence && afterPlacement - atFence < body.Length,
                    "apply rewrote payload bytes after the final fence: " + (afterPlacement - atFence));
                Console.WriteLine($"Apply interval: {afterPlacement - atFence} process bytes written for two {body.Length}-byte files");
            }
            _server.ExpectObjects(digest);
        }

        public void StagedDrift(string drift)
        {
            Fresh();
            _server.Publish(Required());
            Check(Run() == SyncResult.RestartRequired, "owned fixture install failed");
            var state = ReadState();
            const string added = "BepInEx/plugins/new-directory/extra.dll";
            _server.Publish(new ManifestMod { Id = Id, Version = "update" }.Add(added, "extra bytes").Add(First, "new bytes"));
            var changed = false;
            _server.Conditional = () =>
            {
                var staged = Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "*-" + Sha("new bytes") + "-9.data", SearchOption.AllDirectories).Single();
                Check(File.ReadAllText(staged) == "new bytes", "stage not verified before final conditional");
                File.WriteAllText(staged, drift);
                changed = true;
            };
            var progress = new SyncProgress();
            var result = Execute(Plan(), progress);
            Check(changed, "fixture did not change the cache after staging");
            if (result == SyncResult.RestartRequired)
            {
                Check(ReadState() != state && ReadIfPresent(First) == "new bytes" && ReadIfPresent(added) == "extra bytes",
                    "prepared placement installed changed cache bytes or did not commit");
                Console.WriteLine("Late cache change did not affect prepared files");
            }
            else
            {
                Check(result == SyncResult.Failed && progress.Error!.Contains("staged object"), "verified-copy fallback did not reject cache drift");
                Check(ReadState() == state && ReadIfPresent(First) == "abc" && ReadIfPresent(added) == null
                    && !Directory.Exists(Path.GetDirectoryName(Path.Combine(_root, added))), "fallback did not roll back live files/state");
                Console.WriteLine("Verified-copy fallback rejected late cache change");
            }
            Check(!Directory.EnumerateFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Any(), "staged drift left a rollback backup");
            Check(File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "*-" + Sha("extra bytes") + "-11.data", SearchOption.AllDirectories).Single()) == "extra bytes", "valid staged object was lost");
        }

        public void PostHashAdmission(bool republish)
        {
            Fresh();
            Write(First, "abc");
            _server.Publish(Required());
            Check(Run() == SyncResult.UpToDate, "missing original v2 proof");
            var state = ReadState();
            _server.Publish(new ManifestMod { Id = Id, Version = "update" }.Add(First, "new bytes"));
            var plan = Plan();
            var epoch = Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "root.lock", SearchOption.AllDirectories).Single();
            var epochBefore = File.ReadAllText(epoch);
            var progress = new SyncProgress();
            var free = long.MaxValue;
            var recoveryReads = 0;
            var requests = 0;
            _server.ConditionalFault = _ => { Interlocked.Increment(ref requests); return null; };
            SetObserver(progress, "LocalHashChunk", (Action<string>)(p =>
            {
                if (!p.StartsWith("BepInEx/config/Convoy/recovery/", StringComparison.Ordinal) || ++recoveryReads != 2) return;
                if (republish) _server.PublishWithoutClearing(new ManifestMod { Id = Id, Version = "new generation" }.Add(First, "later bytes"));
                else Interlocked.Exchange(ref free, 0);
            }));
            typeof(SyncEngine).GetProperty("AvailableSpace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_engine, (Func<string, long>)(_ => Interlocked.Read(ref free)));
            var result = _engine.ExecuteSync(plan, new List<long> { Id }, new HashSet<long>(), plan.Replacements, progress);
            Check(recoveryReads == 2 && Volatile.Read(ref requests) == 2 && (republish
                ? result == SyncResult.Replan
                : result == SyncResult.Failed && progress.Error!.Contains("free space")), "change during recovery verification reached live apply or missed the final check");
            Check(progress.Snapshot().CanCancel && ReadState() == state && ReadIfPresent(First) == "abc"
                && File.ReadAllText(epoch) == epochBefore && File.ReadAllText(RecoveryCopies().Single()) == "abc"
                && !Directory.EnumerateFiles(_root, "*.convoy-old-*", SearchOption.AllDirectories).Any(), "failed admission mutated state/epoch/live files or removed recovery");
            _server.ExpectObjects(Sha("new bytes"));
        }

        public void DirtyBundleDispatch()
        {
            _server.Publish(Required().Add(Bundle, "old bundle"));
            Check(Run() == SyncResult.RestartRequired, "proof install failed");
            var state = ReadState();
            _server.Publish(Required().Add(Bundle, "mixed bundle"));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var plugin = (ConvoyPlugin)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ConvoyPlugin));
            var pluginType = typeof(ConvoyPlugin);
            void Dispatch() => pluginType.GetMethod("StartRedownload", flags)!.Invoke(plugin, new object[] { true });
            var panel = new ConvoyPanel(null!, () => { }, Dispatch, () => { }, () => false, () => false);
            panel.UpdateState(ConvoyState.Load());
            var panelType = typeof(ConvoyPanel);
            ConvoyState Draft() => (ConvoyState)panelType.GetField("_state", flags)!.GetValue(panel)!;
            Draft().EnabledGroups.Add("pending-choice");
            Draft().SkippedMods.Add(Id + 7);
            panelType.GetField("_stateDirty", flags)!.SetValue(panel, true);
            panelType.GetField("_modsDirty", flags)!.SetValue(panel, true);
            pluginType.GetField("_engine", flags)!.SetValue(plugin, _engine);
            pluginType.GetField("_panel", flags)!.SetValue(plugin, panel);
            var callback = (Action)panelType.GetField("_onRedownloadBundles", flags)!.GetValue(panel)!;
            callback();
            for (var i = 0; i < 2; i++)
            {
                var thread = (Thread)pluginType.GetField("_execThread", flags)!.GetValue(plugin)!;
                Check(thread.Join(8000), "repair worker did not finish");
                var progress = (SyncProgress)pluginType.GetField("_execProgress", flags)!.GetValue(plugin)!;
                Check(progress.Result == SyncResult.Failed && progress.Error!.Contains("normal sync first"), "bundle mismatch not rejected on real dispatch");
                pluginType.GetMethod("Update", flags)!.Invoke(plugin, null);
                Check(ReadState() == state && ReadIfPresent(Bundle) == "old bundle", "bundle dispatch flushed dirty selection or changed payload");
                Check(Draft().EnabledGroups.Contains("pending-choice") && Draft().SkippedMods.Contains(Id + 7)
                    && (bool)panelType.GetField("_stateDirty", flags)!.GetValue(panel)! && (bool)panelType.GetField("_modsDirty", flags)!.GetValue(panel)!, "bundle result refresh lost pending selection delta");
                if (i == 0) ((Action)pluginType.GetField("_retry", flags)!.GetValue(plugin)!)();
            }
            panelType.GetMethod("FlushAutomaticSelection", flags)!.Invoke(panel, new object[] { _engine });
            Check(ReadState() == state, "automatic post-repair update consumed the pending edit");
            var flush = panelType.GetMethod("FlushIfDirty", flags)!;
            flush.Invoke(panel, new object[] { _engine });
            Check(ConvoyState.Load().EnabledGroups.Contains("pending-choice") && ConvoyState.Load().SkippedMods.Contains(Id + 7), "independent manual selection save lost delta");
            _server.ExpectObjects();
        }

        public void MixedBundle()
        {
            _server.Publish(Required().Add(Bundle, "original bundle"));
            Check(Run() == SyncResult.RestartRequired, "install failed");
            _server.ClearRequests();
            var state = ReadState();
            _server.Publish(Required().Add(Bundle, "changed bundle"));
            Check(_engine.ExecuteRedownload(_engine.FetchCatalogPublic(), true) == SyncResult.Failed, "mixed revision accepted");
            Check(ReadState() == state && ReadIfPresent(Bundle) == "original bundle", "mixed revision changed files/state");
            _server.ExpectObjects();
        }

        public void MatchingRepair(bool bundlesOnly)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            Check(Run() == SyncResult.RestartRequired, "install failed");
            _server.ClearRequests();
            var state = ReadState();
            var timestamp = File.GetLastWriteTimeUtc(Path.Combine(_root, First));
            Check(_engine.ExecuteRedownload(_engine.FetchCatalogPublic(), bundlesOnly) == SyncResult.UpToDate, "matching repair rewrote live bytes");
            Check(ReadState() == state && File.GetLastWriteTimeUtc(Path.Combine(_root, First)) == timestamp, "matching repair changed state or plugin");
            _server.ExpectObjects(bundlesOnly ? new[] { Sha("bundle bytes") } : new[] { Abc, Sha("bundle bytes") });
        }

        private string InstallBundleProof()
        {
            Check(Run() == SyncResult.RestartRequired, "failed to create v2 bundle proof");
            var baseline = ReadState();
            File.Delete(Path.Combine(_root, Bundle));
            _server.ClearRequests();
            return baseline;
        }

        public void Repair(bool bundlesOnly, bool fail)
        {
            _server.Publish(Required().Add(Bundle, "bundle bytes"));
            var baseline = bundlesOnly ? InstallBundleProof() : OriginalState;
            if (fail) { _server.Fault = "truncated"; _server.FaultDigest = Sha("bundle bytes"); }
            var result = _engine.ExecuteRedownload(_engine.FetchCatalogPublic(), bundlesOnly);
            Check(result == (fail ? SyncResult.Failed : bundlesOnly ? SyncResult.Applied : SyncResult.RestartRequired), "wrong repair result: " + result);
            Check(ReadIfPresent(Bundle) == (fail ? null : "bundle bytes") && ReadIfPresent(First) == (!bundlesOnly && fail ? null : "abc"), "wrong repair bytes");
            if (fail || bundlesOnly) Check(ReadState() == baseline && ReadIfPresent(Old) == (bundlesOnly ? null : "installed old file"), "unexpected repair state change");
            _server.ExpectObjects((bundlesOnly ? Array.Empty<string>() : new[] { Abc }).Concat(Enumerable.Repeat(Sha("bundle bytes"), fail ? 4 : 1)).ToArray());
        }

        public void OldPublisher(bool redirect)
        {
            _server.Fault = redirect ? "old-publisher-redirect" : "old-publisher";
            try { _engine.PlanSync(); throw new InvalidOperationException("old server accepted"); }
            catch (IOException ex) { Check(ex.Message.Contains("https://github.com/cebarks/convoy#installation"), ex.Message); }
            Check(ReadState() == OriginalState && ReadIfPresent(Old) == "installed old file", "live files changed");
            _server.ExpectOnlyCatalog();
        }

        public void Live(string url, bool matching)
        {
            var address = new Uri(url);
            Check(address.Scheme == "http" && address.IsLoopback && address.AbsolutePath == "/", "live fixture must be a loopback origin");
            Fresh();
            Host(url);
            const string item = "BepInEx/plugins/Test/item.dll";
            const string empty = "EscapeFromTarkov_Data/Managed/Dependency.dll";
            if (matching) { Write(item, "abc"); Write(empty, ""); }
            var result = Run();
            Check(result == (matching ? SyncResult.UpToDate : SyncResult.RestartRequired), "wrong live result: " + result);
            foreach (var expected in new[] { (Path: item, Bytes: "abc", Sha: Abc, Xxh: "78af5f94892f3950"),
                (Path: empty, Bytes: "", Sha: Empty, Xxh: "2d06800538d394c2") })
            {
                var actual = ReadIfPresent(expected.Path);
                Check(actual == expected.Bytes && Sha(actual!) == expected.Sha && Hash(actual!) == expected.Xxh, "live object bytes/checksum mismatch");
                var entry = ConvoyState.Load().Mods.Single().Files.Single(f => f.Path == expected.Path);
                Check(entry.Owned == !matching && entry.Hash == expected.Xxh && entry.Sha256 == expected.Sha && entry.Length == expected.Bytes.Length,
                    "live object state identity/ownership mismatch");
                Console.WriteLine($"LIVE {expected.Path} length={entry.Length} sha256={entry.Sha256} xxh3={entry.Hash} owned={entry.Owned}");
            }
            Check(Plan().Updates.Count == 0, "live follow-up was not a no-op");
        }

        public string? ReadIfPresent(string path) { var full = Path.Combine(_root, path); return File.Exists(full) ? File.ReadAllText(full) : null; }
        public string ReadState() => File.ReadAllText(StateFile);
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
        private const string Prefix = "/quma/convoy/v2/";
        private readonly HttpListener _listener = new HttpListener();
        private readonly Thread _worker;
        private readonly List<Task> _handlers = new List<Task>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private volatile Dictionary<string, byte[]> _objects = new Dictionary<string, byte[]>();
        private JObject _doc = new JObject();
        private volatile string _json = "{}";
        private volatile string _generation = "";
        private volatile bool _large;
        private int _busy;
        public void ExpectOnlyCatalog() => Check(_requests.SequenceEqual(new[] { Prefix + "catalog" }), "unexpected request on old publisher");
        public int CatalogRequests => _requests.Count(p => p == Prefix + "catalog");
        public int ObjectRequestCount => _requests.Count(p => p.Contains("/objects/"));
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Ranges = new System.Collections.Concurrent.ConcurrentQueue<string>();
        public volatile string Fault = "";
        public volatile string FaultDigest = "";
        public volatile Action? Conditional;
        public Action? BodyPrefix;
        public Func<int, string?>? ConditionalFault;
        public Func<string, string?>? ObjectFault;
        public int ConditionalCount;
        public volatile Action? ObjectRequested;
        public string Url { get; }

        public DownloadFixture()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _worker = new Thread(Serve) { IsBackground = true };
            _worker.Start();
        }

        public static JObject ModJson(ManifestMod mod) => JObject.FromObject(new { id = mod.Id, forge_id = (long?)null,
            guid = (string?)null, name = "Fixture", version = mod.Version,
            files = mod.Assets.Where(a => !a.Path.StartsWith("SPT_Runtime/user/cache/bundles/")).Select(Describe),
            bundles = mod.Assets.Where(a => a.Path.StartsWith("SPT_Runtime/user/cache/bundles/")).Select(Describe) });
        private static object Describe((string Path, string Bytes) a) => new { path = a.Path, length = Encoding.UTF8.GetByteCount(a.Bytes), sha256 = Sha(a.Bytes), xxh3_64 = Hash(a.Bytes) };

        public void Publish(params ManifestMod[] mods)
        {
            ClearRequests();
            PublishWithoutClearing(mods);
        }

        public void PublishWithoutClearing(params ManifestMod[] mods)
        {
            _large = false;
            _objects = mods.SelectMany(m => m.Assets).GroupBy(a => Sha(a.Bytes)).ToDictionary(g => g.Key, g => Encoding.UTF8.GetBytes(g.First().Bytes));
            _generation = Sha(Guid.NewGuid().ToString("N"));
            _doc = JObject.FromObject(new { protocol = 2, generation = _generation, spt_version = "4.1.6", quartermaster_version = "fixture",
                groups = mods.GroupBy(m => m.Optional).Select(group => new { slug = group.Key ? "optional" : "default", name = "Fixture", tier = group.Key ? "optional" : "required", mods = group.Select(ModJson) }),
                exclusions = Array.Empty<string>() });
            _json = _doc.ToString(Formatting.None);
        }

        public void PublishLarge()
        {
            Publish(new ManifestMod { Id = Id }.Add(First, "abc"));
            var xxh = new XxHash3();
            var block = new byte[64 * 1024];
            for (var offset = 0; offset < LargeLength; offset += block.Length) xxh.Append(block);
            Edit((doc, _) => doc["groups"]![0]!["mods"]![0]!["files"]![0] = JObject.FromObject(new
            { path = First, length = LargeLength, sha256 = LargeDigest, xxh3_64 = xxh.GetCurrentHashAsUInt64().ToString("x16") }), null);
            _large = true;
        }

        public void Edit(Action<JObject, string>? change, Func<string, string>? edit)
        {
            change?.Invoke(_doc, _generation);
            _json = _doc.ToString(Formatting.None);
            if (edit != null) _json = edit(_json);
        }

        public void ClearRequests() { while (_requests.TryDequeue(out _)) { } while (Ranges.TryDequeue(out _)) { } }
        public void Reset() { _busy = 0; Fault = FaultDigest = ""; Conditional = ObjectRequested = null; ConditionalFault = null; ObjectFault = null; BodyPrefix = null; ConditionalCount = 0; Publish(); }

        public void ExpectObjects(params string[] expected)
        {
            var paths = _requests.ToArray();
            Check(paths.All(p => p == Prefix + "catalog" || p == "/quma/convoy/report" || p.StartsWith(Prefix + "generations/")), "unexpected or legacy request: " + string.Join(", ", paths));
            var actual = paths.Where(p => p.Contains("/objects/")).Select(p => p.Substring(p.LastIndexOf('/') + 1)).OrderBy(p => p).ToArray();
            Check(actual.SequenceEqual(expected.OrderBy(p => p)), "wrong object requests: " + string.Join(", ", actual));
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext request;
                try { request = _listener.GetContext(); }
                catch (HttpListenerException) when (!_listener.IsListening) { return; }
                _handlers.RemoveAll(task => task.Status == TaskStatus.RanToCompletion);
                _handlers.Add(Task.Run(() =>
                {
                    try { Respond(request); }
                    catch (IOException) { request.Response.Abort(); }
                    catch (HttpListenerException) { request.Response.Abort(); }
                }));
            }
        }

        private void Respond(HttpListenerContext request)
        {
            using var response = request.Response;
            var path = request.Request.Url!.AbsolutePath;
            _requests.Enqueue(path);
            if (path == "/quma/convoy/report" && request.Request.HttpMethod == "POST")
            {
                request.Request.InputStream.CopyTo(Stream.Null);
                response.StatusCode = 204;
                return;
            }
            if (request.Request.HttpMethod != "GET") { response.StatusCode = 405; return; }
            if (path == Prefix + "catalog")
            {
                if (Fault == "old-publisher") { response.StatusCode = 404; return; }
                if (Fault == "old-publisher-redirect") { response.StatusCode = 303; response.RedirectLocation = "/quma/login"; return; }
                if (Fault == "preparing") { Error(response, 503, "preparing", "100"); return; }
                if (request.Request.Headers["If-None-Match"] != null)
                {
                    var catalogFault = ConditionalFault?.Invoke(Interlocked.Increment(ref ConditionalCount));
                    if (catalogFault == "blocked-error")
                    {
                        response.StatusCode = 503;
                        response.ContentLength64 = 15;
                        response.OutputStream.WriteByte((byte)'{');
                        response.OutputStream.Flush();
                        BodyPrefix?.Invoke();
                        return;
                    }
                    if (catalogFault != null) { Error(response, 503, catalogFault, catalogFault == "busy" ? "0" : "1"); return; }
                }
                response.Headers["ETag"] = $"\"{_generation}\"";
                if (request.Request.Headers["If-None-Match"] == response.Headers["ETag"])
                {
                    Conditional?.Invoke();
                    if (Fault != "generation") { response.StatusCode = 304; return; }
                }
                var catalog = Encoding.UTF8.GetBytes(_json);
                response.ContentLength64 = catalog.Length;
                response.OutputStream.Write(catalog, 0, catalog.Length);
                return;
            }
            var prefix = $"{Prefix}generations/{_generation}/objects/";
            if (_large && path == prefix + LargeDigest)
            {
                response.Headers["ETag"] = $"\"{LargeDigest}\"";
                response.Headers["Accept-Ranges"] = "bytes";
                response.ContentLength64 = LargeLength;
                var block = new byte[64 * 1024];
                for (var offset = 0; offset < LargeLength; offset += block.Length)
                    response.OutputStream.Write(block, 0, block.Length);
                return;
            }
            if (!path.StartsWith(prefix, StringComparison.Ordinal) && path.StartsWith(Prefix + "generations/", StringComparison.Ordinal))
            {
                response.StatusCode = 409;
                var error = Encoding.UTF8.GetBytes("{\"code\":\"stale_generation\"}");
                response.ContentLength64 = error.Length;
                response.OutputStream.Write(error, 0, error.Length);
                return;
            }
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !_objects.TryGetValue(path.Substring(prefix.Length), out var bytes))
            { response.StatusCode = 404; return; }
            ObjectRequested?.Invoke();
            var digest = path.Substring(prefix.Length);
            var objectFault = ObjectFault?.Invoke(digest);
            if (objectFault != null) { Error(response, 503, objectFault, "1"); return; }
            var fault = digest == FaultDigest ? Fault : "";
            if (fault == "preparing-object" && Interlocked.Increment(ref _busy) == 1) { Error(response, 503, "preparing", "1"); return; }
            if (fault == "busy")
            {
                var count = Interlocked.Increment(ref _busy);
                if (count <= 3) { Error(response, 503, "busy", count == 1 ? "100" : "0"); return; }
            }
            response.Headers["ETag"] = fault == "validator" ? $"W/\"{digest}\"" : $"\"{digest}\"";
            response.Headers["Accept-Ranges"] = "bytes";
            if (fault == "encoding") response.Headers["Content-Encoding"] = "gzip";
            if (fault == "redirect") { response.Redirect(Url + Prefix + "catalog"); return; }
            if (fault == "truncated") response.KeepAlive = false;
            var range = request.Request.Headers["Range"];
            var validator = request.Request.Headers["If-Range"];
            Ranges.Enqueue(range + "|" + validator);
            if (fault == "416" && range != null)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{bytes.Length}";
                response.ContentLength64 = 0;
                return;
            }
            if (fault == "unsolicited")
            {
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes 0-{bytes.Length - 1}/{bytes.Length}";
            }
            if (range != null && range.StartsWith("bytes=") && range.EndsWith("-")
                && validator == "\"" + digest + "\"" && fault != "full")
            {
                var offset = int.Parse(range.Substring(6, range.Length - 7));
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {offset + (fault == "offset" ? -1 : 0)}-{bytes.Length - 1}/{bytes.Length + (fault == "total" ? 1 : 0)}";
                response.ContentLength64 = bytes.Length - offset;
                if (fault != "truncated") response.OutputStream.Write(bytes, offset, bytes.Length - offset);
                else response.KeepAlive = false;
                return;
            }
            response.ContentLength64 = bytes.LongLength;
            if (Fault == "blocked-bodies" || fault == "blocked-body")
            {
                response.OutputStream.Write(bytes, 0, 2);
                response.OutputStream.Flush();
                BodyPrefix?.Invoke();
                response.OutputStream.Write(bytes, 2, bytes.Length - 2);
                return;
            }
            if (fault == "corrupt" || (fault == "corrupt-once" && Interlocked.Increment(ref _busy) == 1)) { bytes = (byte[])bytes.Clone(); bytes[0] ^= 1; }
            response.OutputStream.Write(bytes, 0, fault == "truncated" ? bytes.Length - 1 : bytes.Length);
        }

        private static void Error(HttpListenerResponse response, int status, string code, string delay)
        {
            response.StatusCode = status;
            response.Headers["Retry-After"] = delay;
            var data = Encoding.UTF8.GetBytes("{\"code\":\"" + code + "\"}");
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
        }

        public void Dispose() { _listener.Stop(); _worker.Join(); Task.WaitAll(_handlers.ToArray()); _listener.Close(); }
    }
}
