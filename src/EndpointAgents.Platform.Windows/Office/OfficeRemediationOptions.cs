using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public sealed class OfficeRemediationOptions
{
    public string StagingRoot { get; set; } = OfficeRemediationPaths.StagingRoot;

    public long MaxDownloadBytes { get; set; } = 250 * 1024 * 1024;

    public bool RequireProtectedAcl { get; set; } = true;

    public bool RequireAdministrator { get; set; } = true;

    /// <summary>
    /// Production must use the protected ProgramData staging root with ACL and
    /// administrator checks; only Development may relax them.
    /// </summary>
    public bool IsAllowedFor(bool isDevelopment) =>
        !string.IsNullOrWhiteSpace(StagingRoot)
        && (isDevelopment
            || Path.GetFullPath(StagingRoot).Equals(
                Path.GetFullPath(OfficeRemediationPaths.StagingRoot),
                StringComparison.OrdinalIgnoreCase)
            && RequireProtectedAcl
            && RequireAdministrator);
}
