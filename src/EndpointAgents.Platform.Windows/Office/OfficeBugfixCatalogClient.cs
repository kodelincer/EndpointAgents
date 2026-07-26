using EndpointAgents.Shared.Office;
using System.Net.Http.Json;

namespace EndpointAgents.Platform.Windows.Office;

public interface IOfficeBugfixCatalogClient
{
    Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken);
}

public sealed class OfficeBugfixCatalogClient(Uri hubUri) : IOfficeBugfixCatalogClient
{
    public async Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var uri = new Uri(hubUri, "/api/files/office/bugfix/catalog");
        var catalog = await client.GetFromJsonAsync<OfficeBugfixCatalog>(
            uri, cancellationToken);
        return catalog?.Artifacts ?? [];
    }
}
