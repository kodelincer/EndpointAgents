using EndpointAgents.Shared.Support;

namespace ControlPlane.Api.Support;

public sealed class SupportRequestStore
{
    public const int Capacity = 200;
    private readonly object _gate = new();
    private readonly Queue<SupportRequest> _requests = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSubmissionByAgent =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdd(SupportRequest request, out string? rejection)
    {
        lock (_gate)
        {
            if (_lastSubmissionByAgent.TryGetValue(request.AgentId, out var previous)
                && request.ReceivedAtUtc - previous < TimeSpan.FromSeconds(5))
            {
                rejection = "Wait a few seconds before sending another support request.";
                return false;
            }

            _lastSubmissionByAgent[request.AgentId] = request.ReceivedAtUtc;
            _requests.Enqueue(request);
            while (_requests.Count > Capacity) _requests.Dequeue();
            rejection = null;
            return true;
        }
    }

    public IReadOnlyList<SupportRequest> Get(int limit, string? agentId)
    {
        limit = Math.Clamp(limit, 1, Capacity);
        lock (_gate)
        {
            return _requests.Where(item => string.IsNullOrWhiteSpace(agentId)
                    || item.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                .TakeLast(limit).ToArray();
        }
    }
}

