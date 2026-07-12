using ControlPlane.Api.Agents;
using EndpointAgents.Shared.Agents;
using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Hubs;

public sealed class AgentHub(AgentRegistry registry, ILogger<AgentHub> logger) : Hub
{
    public async Task RegisterAgent(AgentRegistration registration)
    {
        await registry.MarkOnlineAsync(registration, Context.ConnectionId);
        logger.LogInformation("Agent {AgentId} registered with connection {ConnectionId}",
            registration.AgentId, Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await registry.MarkOfflineAsync(Context.ConnectionId);
        logger.LogInformation(exception, "Agent connection disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
