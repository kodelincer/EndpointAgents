using EndpointAgents.Shared.Rpc;

namespace ControlPlane.Api.Dashboard;

/// <summary>One immutable connection or command event published to browser clients.</summary>
public sealed record DashboardActivity(string EventId, string Type, DateTimeOffset TimestampUtc,
    string AgentId, string Status, string? RequestId = null, RpcRequest? Request = null,
    RpcResponse? Response = null, long? DurationMs = null, object? Agent = null);

/// <summary>Stable SignalR event names shared with the browser dashboard.</summary>
public static class DashboardEventTypes
{
    public const string AgentConnected = "agent.connected";
    public const string AgentDisconnected = "agent.disconnected";
    public const string CommandStarted = "command.started";
    public const string CommandCompleted = "command.completed";
}
