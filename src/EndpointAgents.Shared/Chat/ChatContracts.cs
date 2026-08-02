using System.Text.Json;

namespace EndpointAgents.Shared.Chat;

public enum ChatMessageRole
{
    User,
    Assistant,
    System
}

public enum ChatMessageState
{
    Queued,
    Working,
    Completed,
    Failed
}

public enum ChatActionState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Executed,
    Failed
}

public enum ChatProgressStage
{
    Queued,
    AssistantStarted,
    WaitingForModel,
    EndpointCheckStarted,
    EndpointCheckCompleted,
    Retrying,
    Completed,
    Failed
}

public enum ChatProgressActivity
{
    None,
    OfficeInventory,
    OfficeActivationStatus,
    ProposedAction,
    EndpointCheck
}

public enum ChatProgressOutcome
{
    None,
    Success,
    Failure
}

public sealed record ChatIdentity(string UserSid, string UserName, int SessionId);

public sealed record ChatConversation(
    string ConversationId,
    string AgentId,
    string UserSid,
    string UserName,
    int SessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long LastSequence);

public sealed record ChatMessage(
    string MessageId,
    string ConversationId,
    long Sequence,
    ChatMessageRole Role,
    string Body,
    string TextDirection,
    ChatMessageState State,
    DateTimeOffset CreatedAtUtc,
    string? RunId = null);

public sealed record ChatMessageSubmission(
    string ClientMessageId,
    string? ConversationId,
    string MessageText,
    string TextDirection,
    ChatIdentity Identity);

public sealed record ChatMessageReceipt(
    string ConversationId,
    string MessageId,
    long Sequence,
    bool Accepted,
    string Message);

public sealed record ChatDeltaRequest(
    string ConversationId,
    long AfterSequence,
    ChatIdentity Identity);

public sealed record ChatActionProposal(
    string ActionId,
    string ConversationId,
    long Sequence,
    string Method,
    string DisplayName,
    string Description,
    JsonElement? Parameters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ChatActionState State);

public sealed record ChatActionDecision(
    string ConversationId,
    string ActionId,
    bool Approved,
    ChatIdentity Identity);

public sealed record ChatActionDecisionReceipt(
    bool Accepted,
    ChatActionProposal? Proposal,
    string Message);

public sealed record ChatRunProgressEvent(
    string EventId,
    string ConversationId,
    string RunId,
    long Sequence,
    ChatProgressStage Stage,
    ChatProgressActivity Activity,
    ChatProgressOutcome Outcome,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatDelta(
    ChatConversation Conversation,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatActionProposal> Actions,
    IReadOnlyList<ChatRunProgressEvent>? ProgressEvents = null);
