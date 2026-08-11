namespace EndpointAgents.Shared.Windows;

public static class WindowsActivationStates
{
    public const string Licensed = "Licensed";
    public const string Unlicensed = "Unlicensed";
    public const string Grace = "Grace";
    public const string Notification = "Notification";
    public const string Unknown = "Unknown";
}

public static class WindowsRemediationPaths
{
    public const string StagingRoot = @"C:\ProgramData\EndpointAgents\WindowsRemediation";
    public const string ArtifactId = "windows-activation-repair";
    public const string FileName = "windowsbugfixer.bat";
}

public static class WindowsHelloSamplePaths
{
    public const string ArtifactId = "windows-hello-sample";
    public const string FileName = "windows-hello-sample.bat";
}

public sealed record WindowsActivationReport(
    string? ProductName,
    string? EditionId,
    string? DisplayVersion,
    string? Build,
    string Architecture,
    string State,
    int? LicenseStatus,
    string? LicenseStatusReason,
    int? GracePeriodMinutes,
    string? Channel,
    bool ProbeSucceeded,
    string? FailureCode = null);

public sealed record WindowsBugfixArtifactDescriptor(
    string ArtifactId,
    string DisplayName,
    string Description,
    string FileName,
    string Sha256,
    string DownloadPath);

public sealed record WindowsBugfixCatalog(
    IReadOnlyList<WindowsBugfixArtifactDescriptor> Artifacts);

public sealed record WindowsBugfixConsentRequest(
    string ArtifactId,
    string DisplayName,
    string Description);

public sealed record WindowsHelloLaunchRequest(
    string ArtifactId,
    string FilePath,
    string ExpectedSha256,
    bool ChatApproved = false);
