namespace ControlPlane.WebHost.Management;

/// <summary>Lifecycle actions accepted by the local management surface.</summary>
public enum ServiceAction
{
    Start,
    Stop,
    Restart,
    EnterMaintenance,
    ExitMaintenance
}

/// <summary>Normalized Windows service states returned to the web application.</summary>
public enum ManagedServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused
}

/// <summary>Health states derived from API liveness, readiness, and runtime status.</summary>
public enum ApiHealthState
{
    Unknown,
    Healthy,
    Maintenance,
    Degraded,
    Unreachable
}

/// <summary>Subset of API process status consumed by the supervisor.</summary>
public sealed record ApiSystemStatus(
    string Version,
    string MachineName,
    DateTimeOffset StartedAtUtc,
    long UptimeSeconds,
    bool MaintenanceMode,
    int ConnectedAgents,
    int RetainedActivity);

/// <summary>Complete operator-facing status for the managed control-plane service.</summary>
public sealed record ManagedServiceStatus(
    string ServiceName,
    ManagedServiceState ServiceState,
    ApiHealthState ApiHealth,
    ApiSystemStatus? Api,
    ServiceAction? PendingAction,
    IReadOnlyList<ServiceAction> AvailableActions,
    DateTimeOffset CheckedAtUtc,
    string? Detail = null);

/// <summary>Result returned after an attempted lifecycle operation.</summary>
public sealed record ServiceActionResult(bool Accepted, string Message,
    ManagedServiceStatus Status);
