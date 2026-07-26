using EndpointAgents.Shared.Chat;

namespace ControlPlane.Api.Chat;

public sealed record EnrollmentIssue(string EnrollmentCode, DateTimeOffset ExpiresAtUtc);

public sealed record EnrollmentRedemption(
    string AgentId,
    string DeviceToken,
    DateTimeOffset EnrolledAtUtc);

public sealed record EnrollmentRequest(string EnrollmentCode, string? AgentId, string MachineName);

public sealed record ChatRunLease(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserSid,
    string UserName,
    string MachineName,
    string UserMessageId,
    string Capability,
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
    string Message);
