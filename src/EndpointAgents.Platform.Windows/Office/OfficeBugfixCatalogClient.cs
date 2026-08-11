using EndpointAgents.Platform.Windows;
using EndpointAgents.Shared.Office;
using System.Net.Http.Json;

namespace EndpointAgents.Platform.Windows.Office;

public interface IOfficeBugfixCatalogClient
{
    Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken);
}

public sealed class OfficeBugfixCatalogClient(
    EndpointArtifactTransport transport) : IOfficeBugfixCatalogClient
{
    public async Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken)
    {
        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var request = await transport.CreateGetRequestAsync(
            "/api/files/office/bugfix/catalog", cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var catalog = await response.Content.ReadFromJsonAsync<OfficeBugfixCatalog>(
            cancellationToken: cancellationToken);
        return catalog?.Artifacts ?? [];
    }
}
