namespace EndpointAgents.Service;

public sealed class ControlPlaneOptions
{
    // https://agent-hub.shirazmetro.ir
    public string HubUrl { get; set; } = "https://localhost:5001/agent-hub";

    public string AgentId { get; set; } = Environment.MachineName;

    public string Version { get; set; } = "0.1.0";
}