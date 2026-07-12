namespace EndpointAgents.Service;

public sealed class ControlPlaneOptions
{
    public string HubUrl { get; set; } = "https://localhost:5001/agent-hub";

    // Leave blank to use a generated identity persisted under ProgramData.
    public string? AgentId { get; set; }

    public string Version { get; set; } = "0.1.0";

    public int CommandTimeoutSeconds { get; set; } = 60;

    public int MaxConcurrentCommands { get; set; } = 1;
}
