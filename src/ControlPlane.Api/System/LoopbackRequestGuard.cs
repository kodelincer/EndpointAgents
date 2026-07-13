using System.Net;

namespace ControlPlane.Api.System;

/// <summary>Centralizes the local-only boundary for privileged management calls.</summary>
public static class LoopbackRequestGuard
{
    /// <summary>Returns true only when the remote peer is a loopback address.</summary>
    public static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);
}
