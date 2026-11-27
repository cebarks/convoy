using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Convoy
{
    public class SyncOutcome
    {
        public SyncResult Result;
        public string? Error;
        public string? Recovery;
        public string? SptVersion;
        public string? QuartermasterVersion;
        public string? ServerUrl;
    }

    public class SyncProgress
    {
        public sealed class View
        {
            public string Phase { get; internal set; } = "Checking installed files";
            public long Scanned { get; internal set; }
            public long Expected { get; internal set; }
            public long Reused { get; internal set; }
            public long Received { get; internal set; }
            public long Planned { get; internal set; }
            public long BytesPerSecond { get; internal set; }
            public bool CanCancel { get; internal set; } = true;
            public bool CanRetry { get; internal set; }
            public SyncResult? Result { get; internal set; }
            public string? Recovery { get; internal set; }
            public string? Error { get; internal set; }
        }
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private bool _cancelRequested;
        private readonly View _view = new View();
        private readonly Dictionary<string, (long Reused, long New)> _pending = new Dictionary<string, (long, long)>();
        private readonly Func<long> _elapsedMilliseconds;
        internal Action<string>? BeforeLocalObservation { get; set; }
        internal Action<string>? LocalHashChunk { get; set; }
        internal Action? BeforeStateSave { get; set; }
        internal Action<long>? BeforeObjectRead { get; set; }
        public SyncProgress()
        {
            var clock = Stopwatch.StartNew();
            _elapsedMilliseconds = () => clock.ElapsedMilliseconds;
        }
        internal SyncProgress(Func<long> elapsedMilliseconds) => _elapsedMilliseconds = elapsedMilliseconds;
        private readonly Dictionary<string, (long Second, long Bytes)> _wire = new Dictionary<string, (long, long)>();
        internal bool CancellationRequested { get { lock (_gate) return _cancelRequested; } }
        internal CancellationToken Token => _cancel.Token;
        public View Snapshot()
        {
            lock (_gate)
            {
                var second = _elapsedMilliseconds() / 1000;
                return new View { Phase = _view.Phase, Scanned = _view.Scanned, Expected = _view.Expected,
                    Reused = _view.Reused + _pending.Values.Sum(v => v.Reused), Received = _view.Received + _pending.Values.Sum(v => v.New), Planned = _view.Planned,
                    BytesPerSecond = _wire.Values.Where(v => v.Second == second).Sum(v => v.Bytes),
                    CanCancel = _view.CanCancel, CanRetry = _view.CanRetry, Result = _view.Result, Error = _view.Error, Recovery = _view.Recovery };
            }
        }
        public bool TryCancel()
        {
            lock (_gate)
            {
                if (!_view.CanCancel) return false;
                _view.CanCancel = false;
                _cancelRequested = true;
            }
            _cancel.Cancel();
            return true;
        }

        // Only the short baseline state commit is non-cancellable; planning resumes afterward.
        internal void CommitLegacyBaseline(Action commit, CancellationToken cancel = default)
        {
            bool canCancel;
            lock (_gate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_cancelRequested) throw new OperationCanceledException(Token);
                canCancel = _view.CanCancel;
                _view.CanCancel = false;
            }
            try { commit(); }
            finally
            {
                lock (_gate) _view.CanCancel = canCancel && !_view.Result.HasValue && !_cancelRequested;
            }
        }

        internal void BeginApply(CancellationToken cancel = default)
        {
            lock (_gate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_cancelRequested) throw new OperationCanceledException(Token);
                _view.CanCancel = false;
                _view.Phase = "Applying";
            }
        }
        public void SetPhase(string phase) { lock (_gate) _view.Phase = phase; }
        internal void Expect(long files) { lock (_gate) { _view.Expected = files; _view.Scanned = 0; } }
        internal void Scan() { lock (_gate) _view.Scanned++; }
        internal void Plan(long bytes) { lock (_gate) _view.Planned = bytes; }
        internal void StartObject(string key, long reused, long received)
        { lock (_gate) _pending[key] = (reused, received); }
        internal void Accept(string key, long reused, long received)
        { lock (_gate) { _pending.Remove(key); _view.Reused += reused; _view.Received += received; } }
        internal void Receive(string key, long bytes)
        {
            lock (_gate)
            {
                var second = _elapsedMilliseconds() / 1000;
                _wire.TryGetValue(key, out var prior);
                _wire[key] = (second, (prior.Second == second ? prior.Bytes : 0) + bytes);
                var pending = _pending[key];
                _pending[key] = (pending.Reused, pending.New + bytes);
            }
        }
        internal void ResetAttempt(string key) { lock (_gate) { _wire.Remove(key); _pending.Remove(key); } }
        public bool IsComplete => Snapshot().Result.HasValue;
        public SyncResult? Result => Snapshot().Result;
        public string? Error => Snapshot().Error;
        internal void SetRecovery(string recovery) { lock (_gate) _view.Recovery = recovery; }
        internal void SetError(string error) { lock (_gate) _view.Error = error; }
        public void Complete(SyncResult result, string? error = null)
        {
            lock (_gate)
            {
                _view.Result = result;
                if (error != null) _view.Error = error;
                _view.CanCancel = false;
                _view.CanRetry = result == SyncResult.Failed || result == SyncResult.Cancelled || result == SyncResult.Replan;
                _wire.Clear();
                _pending.Clear();
            }
        }
    }
}
