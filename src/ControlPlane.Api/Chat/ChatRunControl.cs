using System.Collections.Concurrent;

namespace ControlPlane.Api.Chat;

/// <summary>
/// In-memory stop switches for the runs this process is working on. The database says a run is
/// <c>Running</c>; this says whether the user asked to stop it and whether it may stop right now.
/// </summary>
public sealed class ChatRunControl
{
    private readonly ConcurrentDictionary<string, RunHandle> _runs = new(StringComparer.Ordinal);

    /// <summary>Starts a fresh handle for a leased run; a retry of the same run replaces the old one.</summary>
    public RunHandle Register(string runId)
    {
        var handle = new RunHandle();
        _runs[runId] = handle;
        return handle;
    }

    public RunHandle? Get(string runId) => _runs.TryGetValue(runId, out var handle) ? handle : null;

    public void Remove(string runId) => _runs.TryRemove(runId, out _);
}

public sealed class RunHandle
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private int _pinned;
    private bool _stopRequested;

    /// <summary>Cancelled once the run may stop: immediately, or when the last pinned operation ends.</summary>
    public CancellationToken Token => _stop.Token;

    public bool StopRequested
    {
        get { lock (_gate) return _stopRequested; }
    }

    /// <summary>Asks the run to stop. Returns false when a pinned operation delays it.</summary>
    public bool RequestStop()
    {
        bool cancelNow;
        lock (_gate)
        {
            _stopRequested = true;
            cancelNow = _pinned == 0;
        }
        if (cancelNow) Cancel();
        return cancelNow;
    }

    /// <summary>
    /// Marks an operation that must not be interrupted (a repair) as running. Fails when the user
    /// already stopped the run, so a stopped request never starts a new repair.
    /// </summary>
    public bool TryPin(out IDisposable? pin)
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                pin = null;
                return false;
            }
            _pinned++;
        }
        pin = new Pin(this);
        return true;
    }

    private void Unpin()
    {
        bool cancelNow;
        lock (_gate)
        {
            _pinned--;
            cancelNow = _stopRequested && _pinned == 0;
        }
        if (cancelNow) Cancel();
    }

    private void Cancel() => _stop.Cancel();

    private sealed class Pin(RunHandle owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Unpin();
        }
    }
}
