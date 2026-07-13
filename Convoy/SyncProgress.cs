using System.Threading;

namespace Convoy
{
    public class SyncOutcome
    {
        public SyncResult Result;
        public string? Error;
        public string? SptVersion;
        public string? QuartermasterVersion;
        public string? ServerUrl;
    }

    public class SyncProgress
    {
        private volatile string _phase = "Initializing...";
        private long _bytesReceived;
        private long _totalBytes;
        private volatile bool _isComplete;
        private SyncResult? _result; // ordering guaranteed by volatile _isComplete fence
        private volatile string? _error;

        public string Phase => _phase;
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public bool IsComplete => _isComplete;
        public SyncResult? Result => _result;
        public string? Error => _error;

        public void SetPhase(string phase)
        {
            Interlocked.Exchange(ref _totalBytes, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            _phase = phase;
        }

        public void SetDownloadProgress(long received, long total)
        {
            Interlocked.Exchange(ref _bytesReceived, received);
            Interlocked.Exchange(ref _totalBytes, total);
        }

        public void Complete(SyncResult result, string? error = null)
        {
            _result = result;
            _error = error;
            _isComplete = true; // must be last — reader checks this first
        }
    }
}
