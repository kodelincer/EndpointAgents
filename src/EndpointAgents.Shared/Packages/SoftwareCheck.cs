namespace EndpointAgents.Shared.Packages;

/// <summary>One requirement rule as the PC checked it.</summary>
/// <param name="Rule">windows-build, edition, architecture, memory, disk, cpu, screen, runtime, domain, conflict.</param>
/// <param name="Message">A plain English sentence for the helpdesk, such as
/// "This PC has 4 GB of memory; the software needs 8 GB."</param>
public sealed record RequirementResult(string Rule, bool Passed, string Needed, string Found, string Message);

/// <summary>What <c>software.check_requirements</c> returns for one install package.</summary>
/// <param name="Met">Every rule passed, counting runtimes that <paramref name="InstallFirst"/> will install.</param>
/// <param name="AlreadyInstalled">A detection rule matched: nothing needs to be installed.</param>
/// <param name="MissingRuntimes">Required runtimes this PC does not have.</param>
/// <param name="InstallFirst">Catalog package ids that provide the missing runtimes, in install order.</param>
public sealed record RequirementReport(
    string PackageId,
    bool Met,
    bool AlreadyInstalled,
    IReadOnlyList<RequirementResult> Results,
    IReadOnlyList<string> MissingRuntimes,
    IReadOnlyList<string> InstallFirst)
{
    /// <summary>The failed rules' messages, for the approval card and the agent.</summary>
    public IReadOnlyList<string> Problems => Results.Where(result => !result.Passed)
        .Select(result => result.Message).ToArray();
}

/// <summary>What an install run returns besides the usual package result.</summary>
public static class SoftwareRunStates
{
    public const string Installed = "Installed";
    public const string AlreadyInstalled = "AlreadyInstalled";
    public const string RequirementsNotMet = "RequirementsNotMet";
    /// <summary>Another installation was running (MSI 1618); try again in a few minutes.</summary>
    public const string RetryLater = "RetryLater";
    public const string NotDetectedAfterInstall = "NotDetectedAfterInstall";
    public const string InstallerFailed = "InstallerFailed";
    public const string ManualOnly = "ManualOnly";
}
