using ControlPlane.Api.Chat;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace ControlPlane.Api.Security;

public static class AgentAuthentication
{
    public const string Scheme = "EndpointDeviceToken";
    public const string AgentIdClaim = "agent_id";
}

public sealed class AgentTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ChatStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadBearerToken();
        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.NoResult();

        var agentId = await store.ValidateDeviceTokenAsync(token, Context.RequestAborted);
        if (agentId is null)
            return AuthenticateResult.Fail("The endpoint device token is invalid or revoked.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, agentId),
            new Claim(AgentAuthentication.AgentIdClaim, agentId)
        ], AgentAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), AgentAuthentication.Scheme));
    }

    private string? ReadBearerToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        if (Request.Path.StartsWithSegments("/agent-hub"))
            return Request.Query["access_token"].FirstOrDefault();
        return null;
    }
}
