using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace Convoy
{
    public class PlannedMod
    {
        public long Id;
        public string Name = "";
        public string Version = "";
        public string? OldVersion;
        public string GroupName = "";
        public string GroupSlug = "";
        public bool IsRequired;
    }

    public sealed class ReplacementConsent
    {
        public string Origin { get; }
        public string Generation { get; }
        public long ModId { get; }
        public string Path { get; }
        public string PriorSha256 { get; }
        public string NextSha256 { get; }

        public ReplacementConsent(string origin, string generation, long modId, string path, string priorSha256, string nextSha256)
        {
            Origin = FileDelivery.Origin(origin).GetLeftPart(UriPartial.Authority);
            Generation = generation;
            ModId = modId;
            Path = path;
            PriorSha256 = priorSha256;
            NextSha256 = nextSha256;
        }

        internal bool Matches(ReplacementConsent other) => Origin == other.Origin && Generation == other.Generation
            && ModId == other.ModId && Path == other.Path && PriorSha256 == other.PriorSha256 && NextSha256 == other.NextSha256;
    }

    public class SyncPlan
    {
        public DeliveryManifest Catalog = null!;
        public ConvoyState State = new ConvoyState();
        internal sealed class ExecutionInput
        {
            internal readonly DeliveryManifest Catalog;
            internal readonly Uri Origin;
            internal readonly ConvoyState.Snapshot Snapshot;
            internal readonly string Epoch;

            internal ExecutionInput(DeliveryManifest catalog, Uri origin, ConvoyState.Snapshot snapshot, string epoch)
            { Catalog = catalog; Origin = origin; Snapshot = snapshot; Epoch = epoch; }
        }

        internal ExecutionInput? Captured { get; }

        public SyncPlan() { }

        internal SyncPlan(ExecutionInput captured)
        {
            Captured = captured;
            Catalog = captured.Catalog;
            ServerUrl = captured.Origin.GetLeftPart(UriPartial.Authority);
        }
        public Dictionary<long, CatalogMod> WantedMods = new Dictionary<long, CatalogMod>();
        public string ServerUrl = "";
        public List<PlannedMod> Installs = new List<PlannedMod>();
        public List<PlannedMod> Updates = new List<PlannedMod>();
        public IReadOnlyList<ReplacementConsent> Replacements { get; internal set; } = Array.Empty<ReplacementConsent>();
        public List<PlannedMod> Removals = new List<PlannedMod>();
    }

    public enum SyncResult { UpToDate, Applied, RestartRequired, Failed, Cancelled, Replan }

    public class SyncEngine
    {
        private const string StatePath = "BepInEx/config/Convoy/state.json";
        private const int DownloadWorkers = 8;
        private readonly ManualLogSource _log;
        private readonly ConvoyConfig _config;
        private readonly string _gameRoot;
        private readonly FileDelivery _delivery = new FileDelivery();

        public SyncEngine(ManualLogSource log, ConvoyConfig config)
        {
            _log = log;
            _config = config;
            _gameRoot = Paths.GameRootPath;
        }

        private static Uri ConfiguredOrigin() => FileDelivery.Origin(SPT.Common.Http.RequestHandler.Host);
        private static string OriginName(Uri origin) => origin.GetLeftPart(UriPartial.Authority);

        private static void CheckStateOrigin(ConvoyState state, Uri origin)
        {
            if (state.Mods.Count == 0) return;
            Uri recorded;
            try { recorded = FileDelivery.Origin(state.ServerUrl); }
            catch (InvalidOperationException) { throw new InvalidOperationException("Convoy install record has no known origin; restore its server URL before syncing"); }
            if (recorded != origin) throw new InvalidOperationException("Convoy install record belongs to another server; use its original SPT origin or a separate game install");
        }

        private SyncFiles Guard(DeliveryManifest catalog, ConvoyState state, Uri origin, CancellationToken cancel)
        {
            if (catalog.Origin != origin) throw new InvalidOperationException("Convoy catalog belongs to another origin; fetch it again");
            CheckStateOrigin(state, origin);
            var files = new SyncFiles(_gameRoot, state.Mods);
            GuardStateTarget(files);
            foreach (var path in catalog.Groups.SelectMany(g => g.Mods).SelectMany(m => m.Assets).Select(a => a.Path).Distinct())
            {
                cancel.ThrowIfCancellationRequested();
                if (Directory.Exists(files.Resolve(path))) throw new IOException("Directory at catalog file: " + path);
            }
            return files;
        }

        private static string GuardStateTarget(SyncFiles files)
        {
            var target = files.Resolve(StatePath);
            if (target != Path.GetFullPath(ConvoyState.FilePath))
                throw new IOException("Unexpected Convoy state target; replan after restoring the game configuration path");
            return target;
        }

        private ConvoyState.Snapshot LoadState() => new ConvoyState.Snapshot(
            GuardStateTarget(new SyncFiles(_gameRoot, Array.Empty<ModState>())));

        private static void RequireStateAndOrigin(SyncFiles files, ConvoyState.Snapshot snapshot, Uri origin)
        {
            snapshot.RequireCurrent(GuardStateTarget(files));
            if (ConfiguredOrigin() != origin) throw new InvalidOperationException("SPT origin changed; replan before applying");
        }

        private static string Identity(CatalogMod mod)
        {
            using var hash = SHA256.Create();
            var canonical = string.Join("\n", mod.Assets.OrderBy(a => a.Path, StringComparer.Ordinal)
                .Select(a => a.Sha256 + " " + a.Xxh3_64 + " " + a.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + a.Path));
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }

        // A grant only describes a particular preimage; authority is derived again from the locked snapshot.
        private static ReplacementConsent? Candidate(DeliveryManifest catalog, ModState? previous, CatalogMod mod,
            DeliveryAsset asset, DeliveryAsset? actual, string nextIdentity)
        {
            if (previous == null || !DeliveryManifest.Hex(previous.Manifest ?? "", 64)
                || (previous.Version == mod.Version && previous.Manifest == nextIdentity)
                || actual == null || actual.Same(asset) || !asset.Path.StartsWith("BepInEx/plugins/", StringComparison.Ordinal)
                || !asset.Path.EndsWith(".dll", StringComparison.Ordinal)) return null;
            var prior = previous.Files.SingleOrDefault(f => f.Path == asset.Path);
            if (prior?.Owned != false || prior.Sha256 != actual.Sha256 || prior.Length != actual.Length || prior.Hash != actual.Xxh3_64)
                return null;
            return new ReplacementConsent(OriginName(catalog.Origin), catalog.Generation, mod.Id, asset.Path, actual.Sha256, asset.Sha256);
        }

        private static bool ExactInventory(ModState installed, CatalogMod published)
        {
            if (installed.Version != published.Version || installed.Manifest != Identity(published)) return false;
            var files = installed.Files.ToLookup(f => f.Path, StringComparer.Ordinal);
            return installed.Files.Count == published.Assets.Count() && published.Assets.All(asset =>
            {
                var records = files[asset.Path];
                if (records.Count() != 1) return false;
                var record = records.Single();
                return record.MatchesV2(asset) || (SyncFiles.Configuration(record.Path)
                    && record.Owned == false && record.Hash == "" && record.Sha256 == null && record.Length == null);
            });
        }

        public SyncPlan? PlanSync(SyncProgress? progress = null)
        {
            var origin = ConfiguredOrigin();
            var cancel = progress?.Token ?? default;
            var catalog = _delivery.Fetch(origin, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            using var spool = TransferSpool.Acquire(_gameRoot, origin, _config.SpoolMaxBytes);
            var snapshot = LoadState();
            var state = snapshot.Parse();
            CheckStateOrigin(state, origin);
            var files = Guard(catalog, state, origin, progress?.Token ?? default);
            var plan = new SyncPlan(new SyncPlan.ExecutionInput(catalog, origin, snapshot, spool.Epoch())) { State = state };
            files.Progress = progress;
            files.Cancel = cancel;
            var replacements = new List<ReplacementConsent>();
            var current = state.Mods.ToDictionary(m => m.Id);
            var inventory = new Dictionary<long, (CatalogGroup Group, CatalogMod Mod)>();
            foreach (var group in catalog.Groups)
                foreach (var mod in group.Mods)
                {
                    cancel.ThrowIfCancellationRequested();
                    inventory.Add(mod.Id, (group, mod));
                    var required = group.Tier == "required";
                    if (!required && !state.EnabledGroups.Contains(group.Slug)) continue;
                    current.TryGetValue(mod.Id, out var existing);
                    var skipped = !required && state.SkippedMods.Contains(mod.Id);
                    if (skipped && existing == null) continue;
                    plan.WantedMods.Add(mod.Id, mod);
                    if (skipped) continue;
                    var row = new PlannedMod { Id = mod.Id, Name = mod.Name, Version = mod.Version,
                        GroupName = group.Name, GroupSlug = group.Slug, IsRequired = required, OldVersion = existing?.Version };
                    var nextIdentity = existing == null ? null : Identity(mod);
                    if (existing == null) plan.Installs.Add(row);
                    else if (existing.Version != mod.Version || existing.Manifest != nextIdentity
                        || mod.Assets.Any(a => !File.Exists(files.Resolve(a.Path))))
                    {
                        plan.Updates.Add(row);
                        var candidates = existing.Files.Where(f => f.Owned == false && f.Sha256 != null)
                            .ToLookup(f => f.Path, StringComparer.Ordinal);
                        foreach (var asset in mod.Files.Where(a => a.Path.StartsWith("BepInEx/plugins/", StringComparison.Ordinal)
                            && a.Path.EndsWith(".dll", StringComparison.Ordinal) && candidates.Contains(a.Path)
                            && candidates[a.Path].Any(f => f.Sha256 != a.Sha256)))
                        {
                            var replacement = Candidate(catalog, existing, mod, asset, files.Observe(asset.Path), nextIdentity!);
                            if (replacement != null) replacements.Add(replacement);
                        }
                    }
                }
            plan.Replacements = replacements.AsReadOnly();
            foreach (var mod in current.Values.Where(m => !plan.WantedMods.ContainsKey(m.Id)))
            {
                inventory.TryGetValue(mod.Id, out var item);
                plan.Removals.Add(new PlannedMod { Id = mod.Id, Name = item.Mod?.Name ?? $"Mod {mod.Id}", Version = mod.Version,
                    GroupName = item.Group?.Name ?? "", GroupSlug = item.Group?.Slug ?? "" });
            }
            cancel.ThrowIfCancellationRequested();
            var groups = _config.RegisterOptionalGroups(catalog.Groups.ToList());
            var groupsChanged = !state.OptionalGroups.Select(g => (g.Slug, g.Name, g.Description))
                .SequenceEqual(groups.Select(g => (g.Slug, g.Name, g.Description)));
            state.OptionalGroups = groups;
            if (plan.Installs.Count + plan.Updates.Count + plan.Removals.Count == 0)
            {
                var skipsChanged = state.SkippedMods.RemoveWhere(id => !inventory.ContainsKey(id)) != 0;
                RequireCurrent(origin, catalog, progress, cancel);
                RequireStateAndOrigin(files, snapshot, origin);
                if (groupsChanged || skipsChanged)
                {
                    spool.RequireFree(StateBytes(state));
                    progress?.BeginApply();
                    spool.AdvanceEpoch();
                    state.Save(snapshot.Target);
                }
                else cancel.ThrowIfCancellationRequested();
                SendReport("up_to_date", state.Mods, null);
            }
            return plan;
        }

        // Panel toggles are deltas, not authority to write the panel's cached ownership record.
        internal void SavePanelSelection(IEnumerable<string> enable, IEnumerable<string> disable,
            IEnumerable<long> skip, IEnumerable<long> unskip)
        {
            var origin = ConfiguredOrigin();
            using var spool = TransferSpool.Acquire(_gameRoot, origin, _config.SpoolMaxBytes);
            var snapshot = LoadState();
            var state = snapshot.Parse();
            CheckStateOrigin(state, origin);
            state.EnabledGroups.UnionWith(enable);
            state.EnabledGroups.ExceptWith(disable);
            state.SkippedMods.UnionWith(skip);
            state.SkippedMods.ExceptWith(unskip);
            spool.RequireFree(StateBytes(state));
            RequireStateAndOrigin(new SyncFiles(_gameRoot, state.Mods), snapshot, origin);
            spool.AdvanceEpoch();
            state.Save(snapshot.Target);
        }

        public DeliveryManifest FetchCatalogPublic() => FetchCatalogForRepair(null);

        internal SyncResult ExecuteFreshRepair(bool bundlesOnly, SyncProgress progress) =>
            ExecuteRedownload(FetchCatalogForRepair(progress), bundlesOnly, progress);

        internal DeliveryManifest FetchCatalogForRepair(SyncProgress? progress) => _delivery.Fetch(ConfiguredOrigin(), progress, progress?.Token ?? default);

        public SyncResult ExecuteRedownload(DeliveryManifest catalog, bool bundlesOnly, SyncProgress? progress = null, CancellationToken cancel = default)
        {
            using var linked = LinkCancellation(progress, cancel);
            cancel = linked?.Token ?? progress?.Token ?? cancel;
            try
            {
                cancel.ThrowIfCancellationRequested();
                var origin = ConfiguredOrigin();
                using var spool = TransferSpool.Acquire(_gameRoot, origin, _config.SpoolMaxBytes);
                var snapshot = LoadState();
                var state = snapshot.Parse();
                var transaction = Guard(catalog, state, origin, cancel);
                RequireStateAndOrigin(transaction, snapshot, origin);
                var ids = new HashSet<long>(state.Mods.Select(m => m.Id));
                var mods = catalog.Groups.SelectMany(g => g.Mods).Where(m => ids.Contains(m.Id)).ToDictionary(m => m.Id);
                if (!bundlesOnly)
                    return ExecuteSyncLocked(spool, new SyncPlan.ExecutionInput(catalog, origin, snapshot, spool.Epoch()), Array.Empty<long>(),
                        mods.Keys.ToList(), state.SkippedMods, Array.Empty<ReplacementConsent>(), progress, cancel, repair: true);
                if (state.Mods.Any(m => !mods.TryGetValue(m.Id, out var published) || !ExactInventory(m, published)))
                    throw new InvalidOperationException("Bundle repair requires an exact installed Convoy revision for every mod; run normal sync first");
                transaction.Progress = progress;
                transaction.Cancel = cancel;
                var incoming = Assets(mods.Values.SelectMany(m => m.Bundles));
                transaction.PreflightWithConsent(incoming, Array.Empty<ModFileState>(), Array.Empty<ModFileState>(), null, true);
                var result = Apply(spool, origin, catalog, transaction, incoming, snapshot, () => { }, progress, cancel, force: true);
                SendReport(result == SyncResult.UpToDate ? "up_to_date" : "updated", state.Mods, null);
                return result;
            }
            catch (Exception ex) { return Failed(ex, progress); }
        }

        private static Dictionary<string, DeliveryAsset> Assets(IEnumerable<DeliveryAsset> entries)
            => entries.GroupBy(a => a.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        private static CancellationTokenSource? LinkCancellation(SyncProgress? progress, CancellationToken cancel) =>
            progress != null && cancel.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(progress.Token, cancel) : null;

        public SyncResult ExecuteSync(SyncPlan plan, List<long> confirmedModIds, HashSet<long> skippedModIds,
            IReadOnlyList<ReplacementConsent> consents, SyncProgress? progress = null, CancellationToken cancel = default)
        {
            using var linked = LinkCancellation(progress, cancel);
            cancel = linked?.Token ?? progress?.Token ?? cancel;
            try
            {
                cancel.ThrowIfCancellationRequested();
                var captured = plan.Captured ?? throw new InvalidOperationException("Missing Convoy execution plan; replan before applying");
                if (ConfiguredOrigin() != captured.Origin || FileDelivery.Origin(plan.ServerUrl) != captured.Origin)
                    throw new InvalidOperationException("SPT origin changed since planning");
                if (!ReferenceEquals(plan.Catalog, captured.Catalog))
                    throw new InvalidOperationException("Convoy catalog changed since planning; replan before applying");
                var displayedRemovals = plan.Removals.Select(m => m.Id).ToArray();
                using var spool = TransferSpool.Acquire(_gameRoot, captured.Origin, _config.SpoolMaxBytes);
                return ExecuteSyncLocked(spool, captured, displayedRemovals, confirmedModIds, skippedModIds, consents.ToArray(), progress, cancel);
            }
            catch (Exception ex) { return Failed(ex, progress); }
        }

        private SyncResult ExecuteSyncLocked(TransferSpool spool, SyncPlan.ExecutionInput captured, IReadOnlyList<long> displayedRemovals,
            List<long> confirmedModIds, HashSet<long> skippedModIds, IReadOnlyList<ReplacementConsent> consents,
            SyncProgress? progress, CancellationToken cancel, bool repair = false)
        {
            var origin = captured.Origin;
            if (spool.Origin != origin || ConfiguredOrigin() != origin)
                throw new InvalidOperationException("SPT origin changed since planning");
            var catalog = captured.Catalog;
            var snapshot = captured.Snapshot;
            var state = snapshot.Parse();
            var transaction = Guard(catalog, state, origin, cancel);
            if (captured.Epoch != spool.Epoch()) throw new FileDelivery.DeliveryFailure("publication changed or game root updated; replan before applying");
            transaction.Progress = progress;
            transaction.Cancel = cancel;
            RequireStateAndOrigin(transaction, snapshot, origin);
            var confirmed = new HashSet<long>(confirmedModIds);
            var groups = catalog.Groups.SelectMany(g => g.Mods.Select(m => (m.Id, Group: g))).ToDictionary(x => x.Id, x => x.Group);
            var published = catalog.Groups.SelectMany(g => g.Mods).ToDictionary(m => m.Id);
            var installed = state.Mods.ToDictionary(m => m.Id);
            var removed = repair ? new HashSet<long>() : new HashSet<long>(installed.Keys.Where(id =>
                !groups.TryGetValue(id, out var group) || (group.Tier != "required" && !state.EnabledGroups.Contains(group.Slug))));
            if (!removed.SetEquals(displayedRemovals) || confirmed.Count != confirmedModIds.Count || confirmed.Any(id =>
                removed.Contains(id) || !groups.TryGetValue(id, out var group) || (repair ? !installed.ContainsKey(id)
                    : group.Tier != "required" && (!state.EnabledGroups.Contains(group.Slug) || state.SkippedMods.Contains(id)))))
                throw new InvalidOperationException("Invalid confirmed mod selection");
            var accepted = published.Values.Where(m => confirmed.Contains(m.Id)).ToList();
            var retained = state.Mods.Where(m => !confirmed.Contains(m.Id) && !removed.Contains(m.Id)).ToList();
            var incoming = Assets(accepted.SelectMany(m => m.Assets));
            var excluded = new HashSet<string>(catalog.Exclusions, StringComparer.OrdinalIgnoreCase);
            var identities = accepted.ToDictionary(m => m.Id, Identity);
            var contributors = accepted.SelectMany(m => m.Assets.Select(a => (a.Path, Mod: m))).ToLookup(x => x.Path, x => x.Mod, StringComparer.Ordinal);
            var used = new HashSet<ReplacementConsent>();
            bool Approved(DeliveryAsset asset, DeliveryAsset actual) => !repair && contributors[asset.Path].All(mod =>
            {
                installed.TryGetValue(mod.Id, out var previous);
                var expected = Candidate(catalog, previous, mod, asset, actual, identities[mod.Id]);
                if (expected == null) return false;
                var grant = consents.FirstOrDefault(item => item.Matches(expected));
                return grant != null && used.Add(grant);
            });
            transaction.PreflightWithConsent(incoming, retained.SelectMany(m => m.Files), state.Mods
                .Where(m => confirmed.Contains(m.Id) || removed.Contains(m.Id)).SelectMany(m => m.Files).Where(f => !excluded.Contains(f.Path)), Approved, repair);
            if (used.Count != consents.Count) throw new IOException("Replacement consent no longer matches the accepted update and installed original; replan");
            var updatedMods = accepted.ToDictionary(m => m.Id, m => new ModState { Id = m.Id, Version = m.Version,
                Manifest = identities[m.Id], Files = m.Assets.Select(transaction.Record).ToList() });
            var next = repair ? state.Mods.Select(m => updatedMods.TryGetValue(m.Id, out var update) ? update : m).ToList()
                : retained.Concat(updatedMods.Values).ToList();
            var skipped = new HashSet<long>(skippedModIds);
            if (!repair)
            {
                skipped.ExceptWith(confirmed);
                skipped.IntersectWith(published.Keys);
            }
            var updated = new ConvoyState { ServerUrl = repair ? state.ServerUrl : OriginName(origin), Mods = next,
                EnabledGroups = state.EnabledGroups, OptionalGroups = repair ? state.OptionalGroups : _config.RegisterOptionalGroups(catalog.Groups.ToList()), SkippedMods = skipped };
            var changedState = JsonConvert.SerializeObject(state) != JsonConvert.SerializeObject(updated);
            var result = Apply(spool, origin, catalog, transaction, incoming, snapshot, () =>
            {
                if (!changedState) return;
                progress?.BeforeStateSave?.Invoke();
                RequireStateAndOrigin(transaction, snapshot, origin);
                updated.Save(snapshot.Target);
            }, progress, cancel, changedState ? StateBytes(updated) : 0, force: repair);
            SendReport(result == SyncResult.UpToDate ? "up_to_date" : "updated", next, null);
            return result;
        }

        private void RequireCurrent(Uri origin, DeliveryManifest catalog, SyncProgress? progress, CancellationToken cancel, FileDelivery.ManifestBudget? budget = null)
        {
            if (ConfiguredOrigin() != origin || !_delivery.IsCurrent(origin, catalog.Generation, progress, cancel, budget))
                throw new FileDelivery.DeliveryFailure("publication changed or is unavailable; replan before applying");
        }

        private static long StateBytes(ConvoyState state) => Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(state, Formatting.Indented));

        internal Func<string, long>? AvailableSpace { get; set; }
        internal Func<string, string>? VolumeIdentity { get; set; }

        private SyncResult Apply(TransferSpool spool, Uri origin, DeliveryManifest catalog, SyncFiles files, Dictionary<string, DeliveryAsset> incoming,
            ConvoyState.Snapshot snapshot, Action saveState, SyncProgress? progress, CancellationToken cancel, long stateBytes = 0, bool force = false)
        {
            var writes = files.Writes.Select(path => incoming[path]).ToArray();
            var unique = (force ? incoming.Values.Where(a => files.RepairEligible(a.Path)) : writes).GroupBy(a => a.Sha256).Select(g => g.First()).ToArray();
            var applyBytes = checked(writes.Sum(a => checked(a.Length + 4096)) + stateBytes + files.RemovalCount * 4096L + 64 * 1024L); // Existing rollback files are renamed, not copied.
            void CheckVolumes() => spool.CheckVolumes(files.Mutations.Concat(files.RecoveryPaths).Select(files.Resolve).Append(snapshot.Target), VolumeIdentity);
            CheckVolumes();
            if (force) foreach (var asset in unique) FileDelivery.PrepareForcedRetrieval(asset, spool);
            spool.Reserve(unique, checked(applyBytes + files.RecoveryBytes));
            progress?.Plan(unique.Sum(a => a.Length));
            var manifestBudget = _delivery.NewManifestBudget();
            var failures = new ConcurrentQueue<Exception>();
            var workerCount = Math.Min(DownloadWorkers, unique.Length);
            var next = workerCount - 1;
            var workers = Enumerable.Range(0, workerCount).Select(first => Task.Run(() =>
            {
                // Each worker reuses its own slot without changing the game's ServicePoint limits.
                var connectionGroup = "io.cebarks.convoy.objects." + first.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (var i = first; ; i = Interlocked.Increment(ref next))
                {
                    if (i >= unique.Length) return;
                    var asset = unique[i];
                    try { _delivery.Stage(origin, catalog.Generation, asset, spool, progress, cancel, manifestBudget, connectionGroup); }
                    catch (Exception ex) { failures.Enqueue(ex); return; }
                    if (!failures.IsEmpty) return;
                }
            })).ToArray();
            Task.WaitAll(workers);
            cancel.ThrowIfCancellationRequested();
            if (failures.TryPeek(out var failure)) throw failure;
            progress?.SetPhase("Verifying");
            files.AdvanceEpoch = spool.AdvanceEpoch;
            void CheckApplyAdmission()
            {
                cancel.ThrowIfCancellationRequested();
                CheckVolumes();
                spool.RequireFree(checked(applyBytes + files.PendingRecoveryBytes), AvailableSpace);
            }
            CheckApplyAdmission();
            files.ApplyWithPrecommit((path, output) => spool.CopyVerified(incoming[path], output), saveState,
                () => RequireCurrent(origin, catalog, progress, cancel, manifestBudget),
                () => RequireStateAndOrigin(files, snapshot, origin), CheckApplyAdmission);
            if (files.RecoveryNotice is string recovery)
            {
                _log.LogInfo(recovery);
                progress?.SetRecovery(recovery);
            }
            return !files.HasChanges ? SyncResult.UpToDate : files.NeedsRestart ? SyncResult.RestartRequired : SyncResult.Applied;
        }

        private SyncResult Failed(Exception ex, SyncProgress? progress)
        {
            var detail = ex.Message;
            if (ex.Data["ConvoyRecovery"] is string recovery)
            {
                detail += "\n" + recovery;
                _log.LogInfo(recovery);
                progress?.SetRecovery(recovery);
            }
            if (ex is OperationCanceledException || progress?.CancellationRequested == true)
            {
                progress?.SetError(detail);
                return SyncResult.Cancelled;
            }
            var stale = ex is FileDelivery.DeliveryFailure failure && (failure.Code == "stale_generation" || failure.Code.StartsWith("publication changed"));
            _log.LogError("Convoy apply failed: " + detail);
            SendReport("failed", null, ex.Message);
            progress?.SetError(detail);
            return stale && progress != null ? SyncResult.Replan : SyncResult.Failed;
        }

        internal void SendReport(string result, List<ModState>? mods, string? error)
        {
            try
            {
                var report = new Dictionary<string, object>
                {
                    ["aid"] = SPT.Common.Http.RequestHandler.SessionId,
                    ["result"] = result,
                    ["client_version"] = VersionInfo.Version,
                };

                if (mods != null)
                    report["mods"] = mods.Select(m => new { id = m.Id, version = m.Version }).ToArray();

                if (error != null)
                    report["error"] = error;

                var json = JsonConvert.SerializeObject(report);
                var request = FileDelivery.Request(ConfiguredOrigin(), "/quma/convoy/report");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 10_000;
                var body = Encoding.UTF8.GetBytes(json);
                request.ContentLength = body.Length;
                using (var s = request.GetRequestStream())
                    s.Write(body, 0, body.Length);
                using (request.GetResponse()) { }
                _log.LogDebug($"Sync report sent: {result}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to send sync report: {ex.Message}");
            }
        }
    }
}
