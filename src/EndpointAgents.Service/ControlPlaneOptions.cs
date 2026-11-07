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

    /// <summary>One-time code used only when no protected device token exists.</summary>
    public string? EnrollmentCode { get; set; }

    /// <summary>Machine-scoped DPAPI file containing the endpoint credential.</summary>
    public string DeviceCredentialPath { get; set; } =
        @"C:\ProgramData\EndpointAgents\Credentials\device-credential.bin";

    /// <summary>
    /// Version advertised when the agent registers and compared by self-update. Defaults to the
    /// build's version (set with -p:Version by scripts\new-agent-update.ps1); set it here only to
    /// override it.
    /// </summary>
    public string Version { get; set; } = BuildVersion;

    public static string BuildVersion { get; } =
        (typeof(ControlPlaneOptions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()
            ?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Maximum duration for one dispatched command, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of commands allowed to execute concurrently.</summary>
    public int MaxConcurrentCommands { get; set; } = 1;
}
