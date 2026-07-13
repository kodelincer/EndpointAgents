using System.Threading;

namespace ControlPlane.Api.System;

/// <summary>
/// Holds process-lifetime operational state that is shared by health checks,
/// command dispatch, and the operations dashboard.
/// </summary>
public sealed class ControlPlaneRuntimeState
{
    private int _maintenanceMode;

    /// <summary>Gets the UTC timestamp at which this API process started.</summary>
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Gets whether new endpoint commands are temporarily disabled.</summary>
    public bool IsMaintenanceMode => Volatile.Read(ref _maintenanceMode) == 1;

    /// <summary>Atomically enters or exits maintenance mode.</summary>
    public void SetMaintenanceMode(bool enabled) =>
        Interlocked.Exchange(ref _maintenanceMode, enabled ? 1 : 0);
}
