using System.Text.Json;

namespace EndpointAgents.Shared.Chat;

public enum ChatMessageRole
{
    User,
    Assistant,
    System,
    Operator
}

public enum ChatMessageState
{
    Queued,
    Working,
    Completed,
    Failed,
    Cancelled
}

public enum ChatActionState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Executed,
    Failed,
    Cancelled
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
    Failed,
    AwaitingApproval,
    ActionStarted,
    ActionCompleted,
    ActionRejected,
    Cancelled
}

public enum ChatProgressActivity
{
    None,
    OfficeInventory,
    OfficeActivationStatus,
    OfficeRuntimeStatus,
    OfficeActivationRepair,
    WindowsActivationStatus,
    WindowsActivationRepair,
    ProposedAction,
    EndpointCheck
}

public enum ChatProgressOutcome
{
    None,
    Success,
    Failure
}

public enum ChatConversationState
{
    Open,
    Working,
    AwaitingApproval,
    Answered,
    Resolved,
    Closed
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

public sealed record ChatConversationListRequest(
    ChatIdentity Identity,
    int Limit = 50);

public sealed record ChatConversationSummary(
    string ConversationId,
    string Title,
    string Preview,
    string TextDirection,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long LastSequence,
    ChatConversationState State,
    bool IsActive)
{
    public bool IsReadOnly => !IsActive;
}

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

public sealed record ChatConversationCloseRequest(
    string ConversationId,
    ChatIdentity Identity);

public sealed record ChatConversationCloseReceipt(
    bool Accepted,
    string Message);

/// <summary>What the Stop button did to the conversation's current request.</summary>
public enum ChatRunStopOutcome
{
    NothingToStop,
    Stopped,
    /// <summary>An operation that cannot be interrupted safely is running; the request stops after it ends.</summary>
    FinishingOperation
}

public sealed record ChatRunStopRequest(
    string ConversationId,
    ChatIdentity Identity);

public sealed record ChatRunStopReceipt(
    bool Accepted,
    ChatRunStopOutcome Outcome,
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
    ChatIdentity Identity,
    string? ResponseText = null);

public sealed record ChatActionDecisionReceipt(
    bool Accepted,
    ChatActionProposal? Proposal,
    string Message,
    ChatMessage? DecisionMessage = null);

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
    IReadOnlyList<ChatRunProgressEvent>? ProgressEvents = null)
{
    public int VisibleItemLimit { get; init; } = 30;
}
