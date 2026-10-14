using EndpointAgents.Shared.Actions;

namespace ControlPlane.Api.Actions;

/// <summary>One environment-map entry an administrator maintains (internal app, printer, share, host).</summary>
public sealed record EnvironmentTarget(
    string Id,
    string Kind,
    string Label,
    string Address,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> ExpectedAddresses,
    string? DriveLetter,
    string? Location,
    string? Notes,
    DateTimeOffset UpdatedAtUtc)
{
    public ResolvedTarget ToResolved() => new(Id, Kind, Label, Address,
        Ports.Count == 0 ? null : Ports,
        ExpectedAddresses.Count == 0 ? null : ExpectedAddresses, DriveLetter);
}

public sealed record EnvironmentTargetInput(
    string Id,
    string Kind,
    string Label,
    string Address,
    IReadOnlyList<int>? Ports,
    IReadOnlyList<string>? ExpectedAddresses,
    string? DriveLetter,
    string? Location,
    string? Notes);

public sealed record ActionSetting(string ActionId, bool Enabled, int? MaxPerHour,
    DateTimeOffset UpdatedAtUtc, string? UpdatedBy);

public sealed record ActionSettingInput(bool Enabled, int? MaxPerHour);

public static class ActionRunSources
{
    public const string Agent = "Agent";
    public const string Operator = "Operator";
}

public sealed record ActionRunRecord(
    string RunId,
    string ActionId,
    string AgentId,
    string? ConversationId,
    string Source,
    string ArgumentsJson,
    string Status,
    string Summary,
    string? ResultJson,
    string? VerificationJson,
    string? UndoJson,
    DateTimeOffset? UndoneAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ActionUsageStats(string ActionId, int Runs, int Succeeded, int Failed,
    int Blocked, DateTimeOffset? LastRunUtc);

public static class IncidentStatus
{
    public const string Active = "Active";
    public const string Resolved = "Resolved";
}

/// <summary>A known outage the helpdesk declared; the agent checks these first.</summary>
public sealed record Incident(
    string IncidentId,
    string Title,
    string Description,
    IReadOnlyList<string> AffectedTargets,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? CreatedBy);

public sealed record IncidentInput(string Title, string? Description,
    IReadOnlyList<string>? AffectedTargets);

public sealed record IncidentPatch(string? Status, string? Title, string? Description,
    IReadOnlyList<string>? AffectedTargets);

public static class SkillStatus
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Published = "Published";
    public const string Retired = "Retired";
}

/// <summary>A Hermes skill written by a helpdesk member in the dashboard.</summary>
public sealed record HelpdeskSkill(
    string Name,
    string Status,
    string Description,
    string Body,
    string Author,
    string? Reviewer,
    int Version,
    int? PublishedVersion,
    string? ReviewNote,
    string? SourceConversationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PublishedAtUtc);

public sealed record HelpdeskSkillVersion(string Name, int Version, string Description,
    string Body, string Author, string? Note, DateTimeOffset CreatedAtUtc);

public sealed record SkillDraftInput(string Name, string Description, string Body,
    string? Note, string? SourceConversationId);

public sealed record SkillUsageStats(string SkillName, int Conversations, int Resolved,
    int Escalated, DateTimeOffset? LastUsedUtc);
