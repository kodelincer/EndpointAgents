namespace ControlPlane.Api.Tools;

/// <summary>What may reach the cloud model in tool results.</summary>
public sealed class PrivacyOptions
{
    /// <summary>
    /// false (default): IP addresses in endpoint_system_info are masked like every other tool
    /// result; the agent still gets the computed flags (APIPA, gateway in subnet, DNS set).
    /// true: the agent sees the raw addresses.
    /// </summary>
    public bool ShareIpAddressesWithAgent { get; set; }
}
