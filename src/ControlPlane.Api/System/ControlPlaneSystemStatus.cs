namespace ControlPlane.Api.System;

/// <summary>Describes the current process and operator-visible API state.</summary>
public sealed record ControlPlaneSystemStatus(
    string Version,
    string MachineName,
    DateTimeOffset StartedAtUtc,
    long UptimeSeconds,
    bool MaintenanceMode,
    int ConnectedAgents,
    int RetainedActivity);

/// <summary>Request body used by the local operator console to change maintenance state.</summary>
public sealed record MaintenanceModeRequest(bool Enabled);
