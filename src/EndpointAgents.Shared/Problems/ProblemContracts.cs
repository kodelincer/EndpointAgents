namespace EndpointAgents.Shared.Problems;

/// <summary>What went wrong. Fixed list: the dashboard filters on it.</summary>
public static class ProblemKinds
{
    public const string Crash = "Crash";
    public const string LoggedError = "LoggedError";
    public const string OperationFailed = "OperationFailed";
    public const string PackageFailed = "PackageFailed";
    public const string HealthCheckFailed = "HealthCheckFailed";
    public const string UpdateRolledBack = "UpdateRolledBack";
    public const string InstallFailed = "InstallFailed";
    public const string UserReported = "UserReported";

    /// <summary>The kinds a PC may send itself; the others are recorded by the control plane.</summary>
    public static bool IsFromEndpoint(string? kind) => kind is Crash or LoggedError;

    public static readonly IReadOnlyList<string> All =
    [
        Crash, LoggedError, OperationFailed, PackageFailed, HealthCheckFailed,
        UpdateRolledBack, InstallFailed, UserReported
    ];
}

public static class ProblemHubMethods
{
    public const string Report = "ReportProblems";
    public const string ChatNotFixed = "ReportChatNotFixed";
}

public static class ProblemLimits
{
    public const int MaxReportsPerHour = 50;
    public const int MaxOutboxBytes = 2 * 1024 * 1024;

    /// <summary>SignalR refuses messages over 32 KB by default, so a batch stays under it.</summary>
    public const int MaxBatchCharacters = 24 * 1024;
    public const int MaxTitleCharacters = 300;
    public const int MaxStackCharacters = 3000;
    public const int MaxStackLines = 25;
    public const int MaxFieldCharacters = 100;
}

/// <summary>
/// Facts about the PC that make a hardware- or Windows-specific bug stand out. No user names,
/// no serial numbers.
/// </summary>
public sealed record ProblemDeviceFacts(
    string? AgentVersion = null,
    string? MachineName = null,
    string? OsName = null,
    string? OsEdition = null,
    string? OsVersion = null,
    string? OsBuild = null,
    string? Architecture = null,
    string? Manufacturer = null,
    string? Model = null);

/// <summary>One kind of problem on one PC, with how often it happened since it was last sent.</summary>
public sealed record ProblemReport(
    string Fingerprint,
    string Kind,
    string Source,
    string Title,
    string? ExceptionType,
    string? Stack,
    string? AgentVersion,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int Count);

public sealed record ProblemReportBatch(ProblemDeviceFacts Device, IReadOnlyList<ProblemReport> Reports);

/// <summary>The user pressed "this did not fix it" in a chat. Carries no chat text.</summary>
public sealed record ChatNotFixedRequest(string ConversationId, EndpointAgents.Shared.Chat.ChatIdentity Identity);

public sealed record ChatNotFixedReceipt(bool Accepted, string Message);

/// <summary>What the installer sends when it fails, before there is an agent or a device token.</summary>
public sealed record InstallFailureReport(
    string? MachineName,
    string? Step,
    string? Message,
    string? AgentVersion,
    string? OsName,
    string? OsVersion,
    string? OsBuild);

public static class ProblemPaths
{
    public static string ReportsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EndpointAgents", "Reports");

    public static string ServiceOutbox => Path.Combine(ReportsRoot, "problem-outbox.json");

    /// <summary>The updater leaves single reports here; the service picks them up.</summary>
    public static string UpdaterInbox => Path.Combine(ReportsRoot, "updater-problems.json");

    public static string InstallLog => Path.Combine(ReportsRoot, "install-failure.log");

    public static string TrayOutbox => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EndpointAgents", "Reports", "tray-problem-outbox.json");
}

public static class AgentLogRoutes
{
    public const string Root = "/api/agent-logs";
    public const string CollectMethod = "diagnostics.collect_logs";
    public const long MaxBundleBytes = 10 * 1024 * 1024;
}
