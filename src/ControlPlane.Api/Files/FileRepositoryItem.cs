using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;

namespace ControlPlane.Api.Files;

/// <summary>Browser-safe metadata for one endpoint-downloadable artifact.</summary>
public sealed record FileRepositoryItem(
    string ArtifactId,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Sha256,
    string DownloadPath,
    string Category = "General",
    string? DisplayName = null,
    string? Description = null,
    string? Kind = null,
    IReadOnlyList<string>? SupportedProductFamilies = null,
    string? MinOfficeVersion = null,
    string? MaxOfficeVersion = null,
    string? MaxOfficeVersionExclusive = null,
    IReadOnlyList<string>? ActivationStates = null,
    IReadOnlyList<string>? ErrorCodes = null,
    bool RequiresInteractiveUser = false,
    bool RequiresOfficeAppsClosed = false,
    bool RequiresRestart = false,
    IReadOnlyList<string>? OfficeProductReleaseIds = null,
    string? MinWindowsVersion = null,
    string? MaxWindowsVersionExclusive = null,
    IReadOnlyList<string>? EditionIds = null,
    IReadOnlyList<OfficeBugfixCompatibility>? AdditionalOfficeCompatibility = null,
    IReadOnlyList<WindowsBugfixCompatibilityRange>? AdditionalWindowsCompatibility = null);
