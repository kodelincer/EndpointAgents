using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Hubs;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace ControlPlane.Api.Agents;

public sealed class AgentRegistry(IHubContext<AgentHub> hubContext, DashboardPublisher dashboard)
{
    private readonly ConcurrentDictionary<string, OnlineAgent> _agentsById = new();
    private readonly ConcurrentDictionary<string, string> _agentIdByConnectionId = new();

    public async Task MarkOnlineAsync(AgentRegistration registration, string connectionId)
    {
        var onlineAgent = new OnlineAgent(registration.AgentId, registration.Hostname,
            registration.MachineName, registration.UserName, registration.Version,
            connectionId, DateTimeOffset.UtcNow);

        if (_agentsById.TryGetValue(registration.AgentId, out var previous))
            _agentIdByConnectionId.TryRemove(previous.ConnectionId, out _);

        _agentsById[registration.AgentId] = onlineAgent;
        _agentIdByConnectionId[connectionId] = registration.AgentId;

        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.AgentConnected, DateTimeOffset.UtcNow, registration.AgentId,
            "online", Agent: onlineAgent.ToDto()));
    }

    public async Task MarkOfflineAsync(string connectionId)
    {
        if (!_agentIdByConnectionId.TryRemove(connectionId, out var agentId)) return;
        if (!_agentsById.TryGetValue(agentId, out var current) || current.ConnectionId != connectionId) return;
        if (!_agentsById.TryRemove(agentId, out var removed)) return;

        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.AgentDisconnected, DateTimeOffset.UtcNow, agentId,
            "offline", Agent: removed.ToDto()));
    }

    public IReadOnlyCollection<OnlineAgentDto> GetOnlineAgents() => _agentsById.Values
        .OrderBy(agent => agent.AgentId).Select(agent => agent.ToDto()).ToArray();

    public async Task<RpcResponse> SendCommandAsync(string agentId, RpcRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.CommandStarted, startedAt, agentId, "pending", request.Id, request),
            cancellationToken);

        RpcResponse response;
        if (!_agentsById.TryGetValue(agentId, out var agent))
        {
            response = RpcResponse.Failure(request.Id, 404, $"Agent '{agentId}' is offline or unknown.");
            await PublishCompletedAsync(agentId, request, response, startedAt, cancellationToken);
            return response;
        }

        try
        {
            response = await hubContext.Clients.Client(agent.ConnectionId)
                .InvokeAsync<RpcResponse>("RunRpc", request, cancellationToken);
        }
        catch (Exception ex)
        {
            await MarkOfflineAsync(agent.ConnectionId);
            response = RpcResponse.Failure(request.Id, 500, "Failed to send command to endpoint agent.",
                new { exception = ex.GetType().Name, message = ex.Message });
        }

        await PublishCompletedAsync(agentId, request, response, startedAt, cancellationToken);
        return response;
    }

    private Task PublishCompletedAsync(string agentId, RpcRequest request, RpcResponse response,
        DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        return dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.CommandCompleted, completedAt, agentId,
            response.Error is null ? "success" : "error", request.Id, request, response,
            (long)(completedAt - startedAt).TotalMilliseconds), cancellationToken);
    }
}

public sealed record OnlineAgent(string AgentId, string Hostname, string MachineName,
    string UserName, string Version, string ConnectionId, DateTimeOffset ConnectedAtUtc)
{
    public OnlineAgentDto ToDto() => new(AgentId, Hostname, MachineName, UserName, Version, ConnectedAtUtc);
}

public sealed record OnlineAgentDto(string AgentId, string Hostname, string MachineName,
    string UserName, string Version, DateTimeOffset ConnectedAtUtc);
