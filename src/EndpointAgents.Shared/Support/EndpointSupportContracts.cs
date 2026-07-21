using EndpointAgents.Shared.Ibsng;

namespace EndpointAgents.Shared.Support;

public static class EndpointAgentProduct
{
    public const string ServiceName = "EndpointAgents.Service";
    public const string ServiceDisplayName = "EndpointAgents Service";
    public const string ServiceEventSource = "EndpointAgents.Service";
    public const string TrayEventSource = "EndpointAgents.Tray";
}

public enum AgentConnectionStatus
{
    Starting,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Faulted
}

public enum AgentFunctionMode
{
    Active,
    Paused
}

public sealed record AgentRuntimeSnapshot(
    string AgentId,
    string MachineName,
    string Version,
    AgentConnectionStatus ConnectionStatus,
    AgentFunctionMode FunctionMode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? LastCommandAtUtc,
    string? LastCommandMethod,
    bool? LastCommandSucceeded,
    int ActiveCommands,
    string? LastProblemCode,
    string? LastProblemMessage,
    DateTimeOffset? LastProblemAtUtc);

public sealed record EndpointLogExcerpt(
    DateTimeOffset TimestampUtc,
    string Level,
    string Source,
    long? EventId,
    string Message);

public sealed record SupportRequestDraft(
    string ClientRequestId,
    string Subject,
    string MessageText,
    string TextDirection,
    int SessionId,
    IReadOnlyList<EndpointLogExcerpt> LogExcerpts,
    IbsngSupportEvidence? IbsngDiagnostics = null);

public sealed record SupportRequestSubmission(
    string ClientRequestId,
    string Subject,
    string MessageText,
    string TextDirection,
    string AgentId,
    string MachineName,
    string UserName,
    int SessionId,
    DateTimeOffset SubmittedAtUtc,
    AgentRuntimeSnapshot Runtime,
    IReadOnlyList<EndpointLogExcerpt> LogExcerpts,
    IbsngSupportEvidence? IbsngDiagnostics = null);

public sealed record SupportRequest(
    string RequestId,
    string ClientRequestId,
    DateTimeOffset ReceivedAtUtc,
    string Subject,
    string MessageText,
    string TextDirection,
    string AgentId,
    string MachineName,
    string UserName,
    int SessionId,
    AgentRuntimeSnapshot Runtime,
    IReadOnlyList<EndpointLogExcerpt> LogExcerpts,
    IbsngSupportEvidence? IbsngDiagnostics = null);

public sealed record SupportRequestReceipt(
    string RequestId,
    DateTimeOffset ReceivedAtUtc,
    bool Accepted,
    string Message);
