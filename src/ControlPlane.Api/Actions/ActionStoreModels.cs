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
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public ResolvedTarget ToResolved() => new(Id, Kind, Label, Address,
        Ports.Count == 0 ? null : Ports,
        ExpectedAddresses.Count == 0 ? null : ExpectedAddresses, DriveLetter,
        Details is { Count: > 0 } ? Details : null);

    public string? Detail(string key) => ToResolved().Detail(key);
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
    string? Notes,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ActionSetting(string ActionId, bool Enabled, int? MaxPerHour,
    DateTimeOffset UpdatedAtUtc, string? UpdatedBy, bool RequiresApproval = false);

/// <param name="RequiresApproval">Make the agent wait for a helpdesk member's approval
/// before this action runs, even when its tier would not.</param>
public sealed record ActionSettingInput(bool Enabled, int? MaxPerHour,
    bool RequiresApproval = false);

public static class ActionRunSources
{
    public const string Agent = "Agent";
    public const string Operator = "Operator";
    /// <summary>An agent request a helpdesk member approved on the Approvals page.</summary>
    public const string Approval = "Approval";
}

public static class ApprovalKinds
{
    /// <summary>A catalogue action (ItApproval tier, or switched to "needs approval").</summary>
    public const string Action = "action";
    /// <summary>A signed package that needs approval (removal packages, or flagged by its manifest).</summary>
    public const string Package = "package";
    /// <summary>Work only a person can do (unlock an account, hand out a key); nothing runs on the PC.</summary>
    public const string Person = "person";
}

/// <summary>The fixed kinds of people-only work the agent may ask the helpdesk for.</summary>
public static class PersonRequestKinds
{
    public sealed record Kind(string Id, string Title, string TitleFa);

    public static readonly IReadOnlyList<Kind> All =
    [
        new("unlock-account", "Unlock the user's account", "باز کردن قفل حساب"),
        new("password-reset", "Reset the user's password", "بازنشانی رمز عبور"),
        new("local-admin", "Temporary administrator rights", "دسترسی موقت مدیر سیستم"),
        new("bitlocker-key", "BitLocker recovery key", "کلید بازیابی BitLocker"),
        new("site-access", "Open a blocked website", "باز کردن دسترسی یک سایت"),
        new("trust-repair", "Rejoin the PC to the domain", "اتصال دوبارهٔ کامپیوتر به دامنه")
    ];

    public static Kind? Find(string? id) =>
        All.FirstOrDefault(kind => string.Equals(kind.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class ApprovalStatus
{
    public const string Pending = "Pending";
    public const string Declined = "Declined";
    /// <summary>Approved and being run on the PC.</summary>
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";
}

/// <summary>A step the agent asked for that waits for a helpdesk member.</summary>
public sealed record ApprovalRequest(
    string ApprovalId,
    string Kind,
    string ItemId,
    string Title,
    string AgentId,
    string? UserSid,
    string? ConversationId,
    string ArgumentsJson,
    string Reason,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecidedBy,
    string? Note,
    string? ResultSummary);

public sealed record ApprovalDecisionInput(string? Note);

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
