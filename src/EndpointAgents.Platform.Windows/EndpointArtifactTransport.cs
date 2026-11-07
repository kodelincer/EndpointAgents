using System.Net.Http.Headers;

namespace EndpointAgents.Platform.Windows;

public interface IEndpointArtifactAuthorizationProvider
{
    Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Creates authenticated, no-redirect requests for endpoint artifacts.</summary>
public sealed class EndpointArtifactTransport(
    Uri hubUri,
    IEndpointArtifactAuthorizationProvider authorization)
{
    public HttpClient CreateClient()
    {
        var origin = new Uri(hubUri.GetLeftPart(UriPartial.Authority));
        if (!origin.IsLoopback && origin.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Remote artifact downloads require HTTPS.");
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>The one upload a PC makes: the logs an admin asked for, to a fixed route.</summary>
    public async Task<HttpRequestMessage> CreateLogUploadRequestAsync(string requestId, HttpContent content,
        CancellationToken cancellationToken)
    {
        var token = await authorization.GetBearerTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "An enrolled endpoint device credential is required to upload logs.");
        var request = new HttpRequestMessage(HttpMethod.Put,
            new Uri(hubUri, $"{EndpointAgents.Shared.Problems.AgentLogRoutes.Root}/{requestId}"))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<HttpRequestMessage> CreateGetRequestAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!path.StartsWith("/api/files/", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
            throw new InvalidDataException("The artifact download path is invalid.");
        var token = await authorization.GetBearerTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "An enrolled endpoint device credential is required for artifact downloads.");
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(hubUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
