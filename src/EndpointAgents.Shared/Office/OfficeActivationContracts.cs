namespace EndpointAgents.Shared.Office;

public static class OfficeActivationStates
{
    public const string Licensed = "Licensed";
    public const string Unlicensed = "Unlicensed";
    public const string Grace = "Grace";
    public const string Notification = "Notification";
    public const string Unknown = "Unknown";
    public const string ToolUnavailable = "ToolUnavailable";
    public const string Error = "Error";
}

public static class OfficeProductFamilies
{
    public const string Microsoft365Apps = "Microsoft365Apps";
    public const string VolumeLicensed = "VolumeLicensed";
    public const string Unknown = "Unknown";
}

public static class OfficeActivationMechanisms
{
    public const string VNext = "VNext";
    public const string Ospp = "OSPP";
}

public static class OfficeLicenseTypes
{
    public const string VolumeKms = "Volume|KMS";
    public const string VolumeMak = "Volume|MAK";
    public const string Retail = "Retail";
    public const string Volume = "Volume";
    public const string Other = "Other";
}

public static class OfficeActivationActions
{
    public const string NoAction = "NoAction";
    public const string ApprovedArtifactAvailable = "ApprovedArtifactAvailable";
    public const string OperatorSelectionRequired = "OperatorSelectionRequired";
    public const string UserSignInOrLicenseRequired = "UserSignInOrLicenseRequired";
    public const string CheckVolumeActivationInfrastructure = "CheckVolumeActivationInfrastructure";
    public const string ManualReview = "ManualReview";
}

public static class OfficeRemediationArtifactKinds
{
    /// <summary>
    /// An Authenticode-signed Microsoft executable that can be launched without
    /// caller-provided command-line arguments.
    /// </summary>
    public const string MicrosoftSignedExecutable = "MicrosoftSignedExecutable";
}

public static class OfficeArtifactTrustPolicies
{
    public const string MicrosoftSigned = "MicrosoftSigned";
    public const string AdministratorApprovedSha256 = "AdministratorApprovedSha256";
}

public static class OfficeRemediationPaths
{
    public const string StagingRoot = @"C:\ProgramData\EndpointAgents\OfficeRemediation";
}

/// <summary>A sanitized Office license result with no identity, tenant, token, or key data.</summary>
public sealed record OfficeActivationProduct(
    string ProductName,
    string? LicenseType,
    string State,
    string? ErrorCode,
    string Source);

/// <summary>Output of one supported Office activation diagnostic tool.</summary>
public sealed record OfficeActivationProbeResult(
    string Mechanism,
    bool ToolAvailable,
    bool Succeeded,
    IReadOnlyList<OfficeActivationProduct> Products,
    string? FailureCode = null);

/// <summary>One administrator-approved remediation artifact exposed by the control plane.</summary>
public sealed record OfficeRemediationArtifactDescriptor(
    string ArtifactId,
    string DisplayName,
    string Description,
    string FileName,
    string Sha256,
    string Kind,
    IReadOnlyList<string> SupportedProductFamilies,
    string? MinOfficeVersion,
    string? MaxOfficeVersion,
    IReadOnlyList<string> ActivationStates,
    IReadOnlyList<string> ErrorCodes,
    bool RequiresInteractiveUser,
    bool RequiresOfficeAppsClosed,
    bool RequiresRestart,
    string DownloadPath);

public sealed record OfficeRemediationCatalog(
    IReadOnlyList<OfficeRemediationArtifactDescriptor> Artifacts);

/// <summary>One additional Office/Windows target for the same BAT and playbook.</summary>
public sealed record OfficeBugfixCompatibility(
    string MinOfficeVersion,
    string MaxOfficeVersionExclusive,
    IReadOnlyList<string> OfficeProductReleaseIds,
    string? MinWindowsVersion = null,
    string? MaxWindowsVersionExclusive = null);

/// <summary>One retailer bugfix executable approved for one or more targets.</summary>
public sealed record OfficeBugfixArtifactDescriptor(
    string ArtifactId,
    string DisplayName,
    string Description,
    string FileName,
    string Sha256,
    string MinOfficeVersion,
    string MaxOfficeVersionExclusive,
    BatchPlaybook Playbook,
    string PlaybookSha256,
    string DownloadPath,
    IReadOnlyList<string>? OfficeProductReleaseIds = null,
    string? MinWindowsVersion = null,
    string? MaxWindowsVersionExclusive = null,
    IReadOnlyList<BatchSupportFile>? SupportFiles = null,
    string? BundleSha256 = null,
    IReadOnlyList<OfficeBugfixCompatibility>? AdditionalCompatibility = null,
    EndpointAgents.Shared.Packages.PackageSignature? Signature = null,
    IReadOnlyList<string>? AcceptedRisks = null);

public sealed record OfficeBugfixCatalog(
    IReadOnlyList<OfficeBugfixArtifactDescriptor> Artifacts);

/// <summary>
/// The only execution data accepted by the Tray. Paths and hashes originate from
/// the endpoint service after it resolves an administrator-approved artifact ID.
/// </summary>
public sealed record OfficeRemediationLaunchRequest(
    string ArtifactId,
    string DisplayName,
    string Description,
    string FilePath,
    string ExpectedSha256,
    string TrustPolicy = OfficeArtifactTrustPolicies.MicrosoftSigned,
    bool ChatApproved = false);

public sealed record OfficeBugfixConsentRequest(
    string ArtifactId,
    string DisplayName,
    string Description);

/// <summary>Stable, AI-ready result returned by software.office_activation_status.</summary>
public sealed record OfficeActivationReport(
    bool OfficeInstalled,
    string? OfficeVersion,
    string? Architecture,
    IReadOnlyList<string> ProductFamilies,
    IReadOnlyList<string> ActivationMechanisms,
    string OverallState,
    IReadOnlyList<OfficeActivationProduct> Products,
    IReadOnlyList<OfficeActivationProbeResult> Probes,
    string RecommendedAction,
    IReadOnlyList<string> CompatibleArtifactIds,
    IReadOnlyList<string> CompatibleBugfixArtifactIds,
    bool RequiresUserInteraction);

/// <summary>Sanitized Office application state for the bound interactive user.</summary>
public sealed record OfficeRuntimeStatus(
    bool InspectionSucceeded,
    bool OfficeApplicationsOpen,
    IReadOnlyList<string> RunningApplications);
