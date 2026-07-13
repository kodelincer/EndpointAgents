using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ControlPlane.Api.System;

/// <summary>Reports the API as unready while operator maintenance mode is active.</summary>
public sealed class MaintenanceReadinessHealthCheck(ControlPlaneRuntimeState runtimeState)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(runtimeState.IsMaintenanceMode
            ? HealthCheckResult.Unhealthy("Control plane is in maintenance mode.")
            : HealthCheckResult.Healthy("Control plane is accepting commands."));
}
