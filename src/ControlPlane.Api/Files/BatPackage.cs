using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;

namespace ControlPlane.Api.Files;

/// <summary>
/// One package: <c>FileRepository/packages/&lt;id&gt;/package.json</c> next to its
/// file and an optional <c>files/</c> folder of supporting files.
/// </summary>
/// <remarks>
/// Schema 1 holds a <see cref="Bat"/> and exactly one of <see cref="Office"/> or
/// <see cref="Windows"/>, which says which activation catalog it joins. Schema 2 is
/// a general repair package: a BAT or EXE in <see cref="File"/>, the
/// <see cref="Symptoms"/> the agent matches against the user's request, and an
/// optional Windows <see cref="Compatibility"/> range.
/// </remarks>
public sealed class BatPackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public BatPackageFile? Bat { get; set; }
    public PackageFileManifest? File { get; set; }
    public List<string> Symptoms { get; set; } = [];
    public PackageCompatibilityManifest? Compatibility { get; set; }
    /// <summary>The administrator's signature over this revision (test-bat-playbook.ps1 -Sign).</summary>
    public EndpointAgents.Shared.Packages.PackageSignature? Signature { get; set; }
    /// <summary>Groups packages that fix the same problem; the agent tries them by <see cref="Order"/>.</summary>
    public string? Problem { get; set; }
    /// <summary>Try order within a problem (lower first), 1-999; defaults to 100.</summary>
    public int? Order { get; set; }
    /// <summary>Wait for a helpdesk member's approval before each run (always true for <c>uninstall-</c> problems).</summary>
    public bool RequiresApproval { get; set; }
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

public sealed class PackageFileManifest
{
    /// <summary><c>bat</c> or <c>exe</c>.</summary>
    public string Type { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Optional exact Authenticode signer subject an EXE must carry.</summary>
    public string? Publisher { get; set; }
}

public sealed class PackageCompatibilityManifest
{
    public string? MinWindowsVersion { get; set; }
    public string? MaxWindowsVersionExclusive { get; set; }
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

    /// <summary>
    /// Lint risks the reviewer accepts for this package: <c>self-elevation</c> and/or
    /// <c>embedded-payload</c>. Both stay inside the signed, hash-pinned file; downloading
    /// code at run time can never be accepted. These are part of what the package is signed
    /// over, so they cannot be changed without signing again.
    /// </summary>
    public List<string> AcceptedRisks { get; set; } = [];
}

public static class BatPackageStatuses
{
    public const string Ready = "Ready";
    public const string ReadyWithWarnings = "ReadyWithWarnings";
    public const string HashMismatch = "HashMismatch";
    public const string InvalidPlaybook = "InvalidPlaybook";
    public const string LintRejected = "LintRejected";
    public const string Invalid = "Invalid";
    /// <summary>Not signed, or not signed by a trusted administrator key.</summary>
    public const string Unsigned = "Unsigned";

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
    BatPackageReview? Review,
    string FileType = "bat",
    IReadOnlyList<string>? Symptoms = null,
    PackageCompatibilityManifest? Compatibility = null,
    string? Signer = null,
    string? ExeSubsystem = null,
    string? Problem = null,
    int Order = BatPackageOrdering.Default);

public static class BatPackageOrdering
{
    public const int Default = 100;
    public const int Minimum = 1;
    public const int Maximum = 999;

    public static bool IsValid(int? order) => order is null or (>= Minimum and <= Maximum);
}
