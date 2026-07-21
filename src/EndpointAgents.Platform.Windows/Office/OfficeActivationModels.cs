using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public sealed record OfficeActivationStatusSnapshot(
    OfficeActivationReport Report,
    IReadOnlyList<OfficeInventoryIssue> Issues,
    IReadOnlyList<OfficeRemediationArtifactDescriptor> ApprovedArtifacts);

public interface IOfficeVolumeActivationProbe
{
    Task<OfficeActivationProbeResult> ProbeAsync(OfficeInventory inventory,
        CancellationToken cancellationToken);
}

public interface IOfficeActivationCatalogClient
{
    Task<IReadOnlyList<OfficeRemediationArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken);
}
