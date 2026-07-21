using Microsoft.AspNetCore.Http;

namespace ControlPlane.Api.System;

/// <summary>Defines the small unauthenticated surface that remote endpoint machines may use.</summary>
public static class OperatorAccessPolicy
{
    /// <summary>Returns true for routes that are intentionally reachable beyond loopback.</summary>
    public static bool AllowsRemoteRequest(PathString path) =>
        path.StartsWithSegments("/agent-hub")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/api/files/ibsng-dialer")
        || path.StartsWithSegments("/api/files/office");
}
