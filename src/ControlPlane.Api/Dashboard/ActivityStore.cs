namespace ControlPlane.Api.Dashboard;

/// <summary>Thread-safe, process-local rolling history for dashboard events.</summary>
public sealed class ActivityStore
{
    public const int Capacity = 500;
    private readonly object _gate = new();
    private readonly Queue<DashboardActivity> _events = new();

    /// <summary>Gets the number of activity records currently retained in memory.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _events.Count;
        }
    }

    /// <summary>Adds an event and evicts the oldest entries above capacity.</summary>
    public void Add(DashboardActivity activity)
    {
        lock (_gate)
        {
            _events.Enqueue(activity);
            while (_events.Count > Capacity) _events.Dequeue();
        }
    }

    /// <summary>Returns the newest bounded snapshot, optionally filtered by agent.</summary>
    public IReadOnlyList<DashboardActivity> Get(int limit, string? agentId)
    {
        limit = Math.Clamp(limit, 1, Capacity);
        lock (_gate)
        {
            return _events.Where(item => string.IsNullOrWhiteSpace(agentId) ||
                    string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .TakeLast(limit).ToArray();
        }
    }
}
