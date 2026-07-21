using System.Collections.Concurrent;

namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>Serializes IBSng mutations for the same interactive desktop.</summary>
public sealed class IbsngCommandCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string desktopPath,
        CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(desktopPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
