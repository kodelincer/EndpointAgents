namespace ControlPlane.WebHost.Management;

/// <summary>Configuration for the local control-plane supervisor.</summary>
public sealed class ManagementOptions
{
    public string ServiceName { get; set; } = "EndpointAgents.ControlPlane.Api";
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5052";
    public int ActionTimeoutSeconds { get; set; } = 30;
}
