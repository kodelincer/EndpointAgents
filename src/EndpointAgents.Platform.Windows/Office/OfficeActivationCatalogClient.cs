using EndpointAgents.Platform.Windows;
using EndpointAgents.Shared.Office;
using System.Net.Http.Json;

namespace EndpointAgents.Platform.Windows.Office;

public sealed class OfficeActivationCatalogClient(
    EndpointArtifactTransport transport) : IOfficeActivationCatalogClient
{
    public async Task<IReadOnlyList<OfficeRemediationArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken)
    {
        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var request = await transport.CreateGetRequestAsync(
            "/api/files/office/catalog", cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var catalog = await response.Content.ReadFromJsonAsync<OfficeRemediationCatalog>(
            cancellationToken: cancellationToken);
        return catalog?.Artifacts ?? [];
    }
}
