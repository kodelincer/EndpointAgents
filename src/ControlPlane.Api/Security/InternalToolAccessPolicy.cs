using ControlPlane.Api.Chat;
using ControlPlane.Api.System;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ControlPlane.Api.Security;

public static class InternalToolAccessPolicy
{
    public static bool IsAllowed(HttpContext context, IOptions<HermesOptions> options)
    {
        if (!LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress))
            return false;
        var expected = options.Value.McpApiKey;
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var token = supplied["Bearer ".Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
    }
}
