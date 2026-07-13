using ControlPlane.Api.Agents;
using EndpointAgents.Shared.Agents;
using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Hubs;

/// <summary>SignalR endpoint used exclusively by installed endpoint agents.</summary>
public sealed class AgentHub(AgentRegistry registry, ILogger<AgentHub> logger) : Hub
{
    /// <summary>Associates the current SignalR connection with a durable agent identity.</summary>
    public async Task RegisterAgent(AgentRegistration registration)
    {
        await registry.MarkOnlineAsync(registration, Context.ConnectionId);
        logger.LogInformation("Agent {AgentId} registered with connection {ConnectionId}",
            registration.AgentId, Context.ConnectionId);
    }

    /// <summary>Removes the disconnected transport without disturbing a newer reconnect.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await registry.MarkOfflineAsync(Context.ConnectionId);
        logger.LogInformation(exception, "Agent connection disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
