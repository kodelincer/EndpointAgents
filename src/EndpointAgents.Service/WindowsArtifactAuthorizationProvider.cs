using EndpointAgents.Platform.Windows;
using Microsoft.Extensions.Options;

namespace EndpointAgents.Service;

public sealed class WindowsArtifactAuthorizationProvider(
    AgentCredentialProvider credentials,
    AgentIdentityProvider identity,
    IOptions<ControlPlaneOptions> options) : IEndpointArtifactAuthorizationProvider
{
    public async Task<string?> GetBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        var agentId = identity.GetOrCreate(options.Value.AgentId);
        return (await credentials.GetOrEnrollAsync(agentId, cancellationToken))?.DeviceToken;
    }
}
