using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Hubs;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace ControlPlane.Api.Agents;

/// <summary>
/// Tracks the live SignalR connection for each logical endpoint agent and routes
/// catalogued RPC requests to the currently registered connection.
/// </summary>
public sealed class AgentRegistry(IHubContext<AgentHub> hubContext, DashboardPublisher dashboard,
    ControlPlane.Api.Problems.ProblemRecorder? problems = null)
{
    private readonly ConcurrentDictionary<string, OnlineAgent> _agentsById = new();
    private readonly ConcurrentDictionary<string, string> _agentIdByConnectionId = new();

    /// <summary>Registers or replaces an agent's current SignalR connection.</summary>
    public async Task MarkOnlineAsync(AgentRegistration registration, string connectionId)
    {
        var onlineAgent = new OnlineAgent(registration.AgentId, registration.Hostname,
            registration.MachineName, registration.UserName, registration.Version,
            connectionId, DateTimeOffset.UtcNow);

        // A reconnect creates a new connection id. Remove the reverse mapping for
        // the stale transport before publishing the replacement as online.
        if (_agentsById.TryGetValue(registration.AgentId, out var previous))
            _agentIdByConnectionId.TryRemove(previous.ConnectionId, out _);

        _agentsById[registration.AgentId] = onlineAgent;
        _agentIdByConnectionId[connectionId] = registration.AgentId;

        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.AgentConnected, DateTimeOffset.UtcNow, registration.AgentId,
            "online", Agent: onlineAgent.ToDto()));
    }

    /// <summary>Removes an agent only when the disconnected transport is still current.</summary>
    public async Task MarkOfflineAsync(string connectionId)
    {
        if (!_agentIdByConnectionId.TryRemove(connectionId, out var agentId)) return;
        // Ignore a late disconnect from an older transport after the same agent
        // has already reconnected with a new connection id.
        if (!_agentsById.TryGetValue(agentId, out var current) || current.ConnectionId != connectionId) return;
        if (!_agentsById.TryRemove(agentId, out var removed)) return;

        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.AgentDisconnected, DateTimeOffset.UtcNow, agentId,
            "offline", Agent: removed.ToDto()));
    }

    /// <summary>Returns a stable public snapshot without server connection ids.</summary>
    public IReadOnlyCollection<OnlineAgentDto> GetOnlineAgents() => _agentsById.Values
        .OrderBy(agent => agent.AgentId).Select(agent => agent.ToDto()).ToArray();

    public bool TryGetByConnectionId(string connectionId, out OnlineAgent? agent)
    {
        agent = null;
        return _agentIdByConnectionId.TryGetValue(connectionId, out var agentId)
            && _agentsById.TryGetValue(agentId, out agent)
            && agent.ConnectionId == connectionId;
    }

    public async Task RevokeAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!_agentsById.TryRemove(agentId, out var removed)) return;
        _agentIdByConnectionId.TryRemove(removed.ConnectionId, out _);
        await dashboard.PublishAsync(new DashboardActivity(Guid.NewGuid().ToString("N"),
            DashboardEventTypes.AgentDisconnected, DateTimeOffset.UtcNow, agentId,
            "revoked", Agent: removed.ToDto()), cancellationToken);
    }

    /// <summary>Sends one RPC request and publishes its complete activity lifecycle.</summary>
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped waiting. Only a read-only check is told to stop on the PC;
            // a repair that already started is left to finish.
            if (RpcInterruptPolicy.CanInterrupt(request.Method))
                await CancelOnEndpointBestEffortAsync(agent.ConnectionId, request.Id);
            await PublishCompletedAsync(agentId, request, RpcResponse.Failure(request.Id, 499,
                "Command was cancelled."), startedAt, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await MarkOfflineAsync(agent.ConnectionId);
            response = RpcResponse.Failure(request.Id, 500, "Failed to send command to endpoint agent.",
                new { exception = ex.GetType().Name, message = ex.Message });
        }

        await PublishCompletedAsync(agentId, request, response, startedAt, cancellationToken);
        await RecordFailureAsync(agent, request, response);
        return response;
    }

    /// <summary>
    /// A failed or timed-out operation goes to the Problems page. Busy, paused and cancelled are
    /// normal answers, not bugs.
    /// </summary>
    private async Task RecordFailureAsync(OnlineAgent agent, RpcRequest request, RpcResponse response)
    {
        if (problems is null) return;
        if (ControlPlane.Api.Problems.ProblemRecorder.RanButDidNotFix(request.Method, response))
        {
            await problems.RecordAsync(agent.AgentId, agent.MachineName, agent.Version,
                EndpointAgents.Shared.Problems.ProblemKinds.OperationFailed, request.Method,
                $"{request.Method} finished, but the problem is still there.", "not-resolved");
            return;
        }
        if (response.Error is not { } error || error.Code is 429 or 499 or 503) return;
        // The user said no to a repair: their choice, not a bug.
        if (error.Message == "UserDeclined") return;
        var kind = request.Method == "system.package_run"
            ? EndpointAgents.Shared.Problems.ProblemKinds.PackageFailed
            : EndpointAgents.Shared.Problems.ProblemKinds.OperationFailed;
        await problems.RecordAsync(agent.AgentId, agent.MachineName, agent.Version, kind, request.Method,
            $"{request.Method} failed with code {error.Code}: {error.Message}", $"code-{error.Code}");
    }

    private async Task CancelOnEndpointBestEffortAsync(string connectionId, string requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hubContext.Clients.Client(connectionId)
                .SendAsync(RpcInterruptPolicy.CancelMethod, requestId, timeout.Token);
        }
        catch (Exception)
        {
            // The check still ends on the endpoint's own command timeout.
        }
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

/// <summary>Internal agent record including the SignalR connection used for routing.</summary>
public sealed record OnlineAgent(string AgentId, string Hostname, string MachineName,
    string UserName, string Version, string ConnectionId, DateTimeOffset ConnectedAtUtc)
{
    public OnlineAgentDto ToDto() => new(AgentId, Hostname, MachineName, UserName, Version, ConnectedAtUtc);
}

/// <summary>Browser-safe representation of an online endpoint agent.</summary>
public sealed record OnlineAgentDto(string AgentId, string Hostname, string MachineName,
    string UserName, string Version, DateTimeOffset ConnectedAtUtc);
