using EndpointAgents.Shared.Office;
using System.Net.Http.Json;

namespace EndpointAgents.Platform.Windows.Office;

public sealed class OfficeActivationCatalogClient(Uri hubUri) : IOfficeActivationCatalogClient
{
    public async Task<IReadOnlyList<OfficeRemediationArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var uri = new Uri(hubUri, "/api/files/office/catalog");
        var catalog = await client.GetFromJsonAsync<OfficeRemediationCatalog>(uri,
            cancellationToken);
        return catalog?.Artifacts ?? [];
    }
}
