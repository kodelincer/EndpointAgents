namespace ControlPlane.Api.Dashboard;

public sealed class ActivityStore
{
    public const int Capacity = 500;
    private readonly object _gate = new();
    private readonly Queue<DashboardActivity> _events = new();

    public void Add(DashboardActivity activity)
    {
        lock (_gate)
        {
            _events.Enqueue(activity);
            while (_events.Count > Capacity) _events.Dequeue();
        }
    }

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
