using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;

namespace ControlPlane.Api.Files;

public sealed class WindowsBugfixManifest
{
    public int Version { get; set; } = 1;
    public List<WindowsBugfixManifestEntry> Artifacts { get; set; } = [];
}

public sealed class WindowsBugfixManifestEntry
{
    public string ArtifactId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string MinWindowsVersion { get; set; } = string.Empty;
    public string MaxWindowsVersionExclusive { get; set; } = string.Empty;
    public List<string> EditionIds { get; set; } = [];
    public List<WindowsBugfixCompatibilityRange> AdditionalCompatibility { get; set; } = [];
    public List<BatchSupportFileManifestEntry> SupportFiles { get; set; } = [];
    public BatchPlaybook? Playbook { get; set; }
}

public sealed class BatchSupportFileManifestEntry
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
