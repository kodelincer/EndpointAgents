using EndpointAgents.Shared.Office;

namespace ControlPlane.Api.Files;

public sealed class OfficeArtifactManifest
{
    public int Version { get; set; } = 1;
    public List<OfficeArtifactManifestEntry> Artifacts { get; set; } = [];
}

public sealed class OfficeArtifactManifestEntry
{
    public string ArtifactId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public List<string> SupportedProductFamilies { get; set; } = [];
    public string? MinOfficeVersion { get; set; }
    public string? MaxOfficeVersion { get; set; }
    public List<string> ActivationStates { get; set; } = [];
    public List<string> ErrorCodes { get; set; } = [];
    public bool RequiresInteractiveUser { get; set; }
    public bool RequiresOfficeAppsClosed { get; set; }
    public bool RequiresRestart { get; set; }
}

public sealed class OfficeBugfixManifest
{
    public int Version { get; set; } = 1;
    public List<OfficeBugfixManifestEntry> Artifacts { get; set; } = [];
}

public sealed class OfficeBugfixManifestEntry
{
    public string ArtifactId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string MinOfficeVersion { get; set; } = string.Empty;
    public string MaxOfficeVersionExclusive { get; set; } = string.Empty;
    public List<string> OfficeProductReleaseIds { get; set; } = [];
    public string? MinWindowsVersion { get; set; }
    public string? MaxWindowsVersionExclusive { get; set; }
    public List<OfficeBugfixCompatibility> AdditionalCompatibility { get; set; } = [];
    public List<BatchSupportFileManifestEntry> SupportFiles { get; set; } = [];
    public BatchPlaybook? Playbook { get; set; }
}

public sealed record FileRepositoryIssue(string Source, string Message);
