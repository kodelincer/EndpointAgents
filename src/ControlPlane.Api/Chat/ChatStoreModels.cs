using EndpointAgents.Shared.Chat;
using System.Text.Json;

namespace ControlPlane.Api.Chat;

public sealed record EnrollmentIssue(string EnrollmentCode, DateTimeOffset ExpiresAtUtc);

public sealed record EnrollmentRedemption(
    string AgentId,
    string DeviceToken,
    DateTimeOffset EnrolledAtUtc);

public sealed record EnrollmentRequest(string EnrollmentCode, string? AgentId, string MachineName);

/// <summary>A valid enrollment code asked for a device id that is enrolled and not revoked.</summary>
public sealed class DeviceAlreadyEnrolledException(string agentId)
    : Exception($"Device {agentId} is already enrolled.")
{
    public string AgentId { get; } = agentId;
}

public sealed record ChatRunLease(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserSid,
    string UserName,
    string MachineName,
    string UserMessageId,
    string Capability,
    int Attempt,
    IReadOnlyList<ChatMessage> History);

public sealed record ChatRunScope(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserSid,
    DateTimeOffset ExpiresAtUtc);

public sealed record ActionDecisionResult(
    bool Accepted,
    ChatActionProposal? Proposal,
    string Message,
    ChatMessage? DecisionMessage = null);

public static class HelpdeskStatus
{
    public const string New = "New";
    public const string Open = "Open";
    public const string Resolved = "Resolved";
    /// <summary>The agent could not solve the problem and handed it to the helpdesk team.</summary>
    public const string Escalated = "Escalated";
    public static bool IsValid(string? value) => value is New or Open or Resolved or Escalated;
}

public static class HelpdeskResponder
{
    public const string Hermes = "Hermes";
    public const string Operator = "Operator";
}

public sealed record HelpdeskConversationSummary(
    string ConversationId,
    string AgentId,
    string UserName,
    int SessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long LastSequence,
    string Status,
    string ResponderMode,
    int UnreadCount,
    string Preview,
    string TextDirection,
    string MachineName = "",
    string Version = "",
    bool IsOnline = false,
    string? EscalationReason = null,
    string? EscalationSummary = null,
    DateTimeOffset? EscalatedAtUtc = null);

public sealed record HelpdeskTraceEvent(
    string EventId,
    string ConversationId,
    long? Sequence,
    DateTimeOffset TimestampUtc,
    string Direction,
    string EventType,
    string Outcome,
    JsonElement Payload);

public sealed record HelpdeskConversationDetail(
    HelpdeskConversationSummary Conversation,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatActionProposal> Actions,
    IReadOnlyList<ChatRunProgressEvent> ProgressEvents,
    IReadOnlyList<HelpdeskTraceEvent> Trace);

public sealed record HelpdeskOperatorMessageRequest(
    string ClientMessageId,
    string MessageText,
    string TextDirection);

public sealed record HelpdeskConversationPatch(
    string? Status,
    bool? OperatorTakeover,
    long? ReadThroughSequence);

public sealed record HelpdeskDiagnosticRequest(string Method, JsonElement? Parameters);

public sealed record HelpdeskChange(
    string ConversationId,
    string ChangeType,
    long? LastSequence = null);
