using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;

namespace ControlPlane.Api.Files;

/// <summary>
/// One BAT package: <c>FileRepository/packages/&lt;id&gt;/package.json</c> next to its
/// BAT and an optional <c>files/</c> folder of supporting files. Exactly one of
/// <see cref="Office"/> or <see cref="Windows"/> says which catalog it joins.
/// </summary>
public sealed class BatPackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public BatPackageFile? Bat { get; set; }
    public List<BatchSupportFileManifestEntry> SupportFiles { get; set; } = [];
    public BatchPlaybook? Playbook { get; set; }
    public string PlaybookSha256 { get; set; } = string.Empty;
    public BatPackageOffice? Office { get; set; }
    public BatPackageWindows? Windows { get; set; }
    public BatPackageReview? Review { get; set; }
}

public sealed class BatPackageFile
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class BatPackageOffice
{
    public string MinOfficeVersion { get; set; } = string.Empty;
    public string MaxOfficeVersionExclusive { get; set; } = string.Empty;
    public List<string> OfficeProductReleaseIds { get; set; } = [];
    public string? MinWindowsVersion { get; set; }
    public string? MaxWindowsVersionExclusive { get; set; }
    public List<OfficeBugfixCompatibility> AdditionalCompatibility { get; set; } = [];
}

public sealed class BatPackageWindows
{
    public string MinWindowsVersion { get; set; } = string.Empty;
    public string MaxWindowsVersionExclusive { get; set; } = string.Empty;
    public List<string> EditionIds { get; set; } = [];
    public List<WindowsBugfixCompatibilityRange> AdditionalCompatibility { get; set; } = [];
}

public sealed class BatPackageReview
{
    public string? ReviewedBy { get; set; }
    public string? ReviewedOn { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
}

public static class BatPackageStatuses
{
    public const string Ready = "Ready";
    public const string ReadyWithWarnings = "ReadyWithWarnings";
    public const string HashMismatch = "HashMismatch";
    public const string InvalidPlaybook = "InvalidPlaybook";
    public const string LintRejected = "LintRejected";
    public const string Invalid = "Invalid";

    public static bool IsPublished(string status) =>
        status is Ready or ReadyWithWarnings;
}

/// <summary>Operator view of one package folder and why it is or is not published.</summary>
public sealed record BatPackageStatus(
    string Id,
    string Folder,
    string Status,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    string? Kind,
    string? DisplayName,
    string? Description,
    string? BatFileName,
    string? PinnedBatSha256,
    string? ActualBatSha256,
    long? BatSizeBytes,
    string? PinnedPlaybookSha256,
    string? ComputedPlaybookSha256,
    BatchPlaybook? Playbook,
    IReadOnlyList<BatchSupportFileManifestEntry> SupportFiles,
    BatPackageOffice? Office,
    BatPackageWindows? Windows,
    BatPackageReview? Review);
