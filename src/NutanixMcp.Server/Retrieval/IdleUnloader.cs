namespace NutanixMcp.Server.Retrieval;

// Lazily creates an expensive IDisposable (an ONNX session holding GBs of weights)
// and disposes it once nothing has used it for `idleTimeout`, so an idle MCP process
// drops back to its baseline footprint instead of pinning the models forever. The
// next use recreates it. In-flight uses are counted, so the instance is never
// disposed under a running call. Create and dispose both run under the gate: a call
// arriving mid-unload waits instead of loading a second copy next to the old one.
public sealed class IdleUnloader<T> : IDisposable
    where T : class, IDisposable
{
    private readonly Func<T> _factory;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private readonly Lock _gate = new();
    private T? _instance;
    private int _inFlight;
    private long _lastUse;
    private bool _disposed;

    // `Timeout.InfiniteTimeSpan` disables unloading (the instance lives until Dispose).
    public IdleUnloader(Func<T> factory, TimeSpan idleTimeout, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        if (idleTimeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        }

        _factory = factory;
        _idleTimeout = idleTimeout;
        _time = time;
        if (idleTimeout != Timeout.InfiniteTimeSpan)
        {
            _timer = time.CreateTimer(
                static s => ((IdleUnloader<T>)s!).OnIdleTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _instance is not null;
            }
        }
    }

    public async Task<TResult> UseAsync<TResult>(Func<T, Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        T instance = Acquire();
        try
        {
            return await action(instance).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _instance?.Dispose();
            _instance = null;
        }
    }

    private T Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instance ??= _factory();
            _inFlight++;
            return _instance;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _inFlight--;
            _lastUse = _time.GetTimestamp();
            if (_inFlight == 0 && !_disposed)
            {
                _timer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnIdleTimer()
    {
        lock (_gate)
        {
            // A use that started after the timer was armed either still runs
            // (_inFlight > 0) or re-armed the timer on release (elapsed < timeout).
            if (_disposed || _inFlight > 0 || _instance is null
                || _time.GetElapsedTime(_lastUse) < _idleTimeout)
            {
                return;
            }

            _instance.Dispose();
            _instance = null;
        }
    }
}
