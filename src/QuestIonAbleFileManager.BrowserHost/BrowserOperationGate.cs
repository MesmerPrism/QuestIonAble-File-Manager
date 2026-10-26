namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>One active Core operation across browser adapters. Admission never queues work.</summary>
public sealed class BrowserOperationGate
{
    private readonly object _sync = new();
    private object? _active;

    public bool IsBusy { get { lock (_sync) return _active is not null; } }

    public IDisposable? TryAcquire()
    {
        lock (_sync)
        {
            if (_active is not null) return null;
            var token = new object();
            _active = token;
            return new Scope(() =>
            {
                lock (_sync)
                    if (ReferenceEquals(_active, token)) _active = null;
            });
        }
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
