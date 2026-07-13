namespace EndpointAgents.Service;

/// <summary>Configuration for the control-plane connection and command limits.</summary>
public sealed class ControlPlaneOptions
{
    /// <summary>Absolute HTTP or HTTPS URL of the control-plane SignalR hub.</summary>
    public string HubUrl { get; set; } = "https://localhost:5001/agent-hub";

    /// <summary>
    /// Optional fixed identity. When blank, an identity is generated and persisted
    /// under the common application-data directory.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>Version advertised when the agent registers.</summary>
    public string Version { get; set; } = "0.1.0";

    /// <summary>Maximum duration for one dispatched command, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of commands allowed to execute concurrently.</summary>
    public int MaxConcurrentCommands { get; set; } = 1;
}
