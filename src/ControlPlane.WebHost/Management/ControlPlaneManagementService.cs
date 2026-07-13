using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace ControlPlane.WebHost.Management;

/// <summary>
/// Serializes privileged service actions and combines SCM state with HTTP health
/// so the dashboard can distinguish a stopped process from an unhealthy API.
/// </summary>
public sealed class ControlPlaneManagementService(
    IWindowsServiceController serviceController,
    IHttpClientFactory httpClientFactory,
    IOptions<ManagementOptions> options,
    ILogger<ControlPlaneManagementService> logger)
{
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly object _stateGate = new();
    private ServiceAction? _pendingAction;

    /// <summary>Gets the latest Windows service and API health snapshot.</summary>
    public async Task<ManagedServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ManagedServiceState serviceState;
        try
        {
            serviceState = await serviceController.GetStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not query Windows service {ServiceName}.",
                options.Value.ServiceName);
            serviceState = ManagedServiceState.Unknown;
        }

        var (health, api, detail) = await ProbeApiAsync(serviceState, cancellationToken);
        ServiceAction? pending;
        lock (_stateGate) pending = _pendingAction;

        return new ManagedServiceStatus(options.Value.ServiceName, serviceState, health, api,
            pending, GetAvailableActions(serviceState, health, pending), DateTimeOffset.UtcNow, detail);
    }

    /// <summary>Executes one lifecycle action if no other action is already running.</summary>
    public async Task<ServiceActionResult> ExecuteAsync(ServiceAction action,
        CancellationToken cancellationToken)
    {
        if (!await _actionGate.WaitAsync(TimeSpan.Zero, cancellationToken))
            return new ServiceActionResult(false, "Another service action is already running.",
                await GetStatusAsync(cancellationToken));

        lock (_stateGate) _pendingAction = action;
        string message;
        var accepted = true;

        try
        {
            var before = await GetStatusAsync(cancellationToken);
            // The pending marker intentionally hides actions from other callers;
            // validate this action against the underlying state without that marker.
            var allowedBeforePending = GetAvailableActions(before.ServiceState, before.ApiHealth, null);
            if (!allowedBeforePending.Contains(action))
            {
                accepted = false;
                message = $"{action} is not available while the service is {before.ServiceState}.";
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ActionTimeoutSeconds));
                await ExecuteCoreAsync(action, timeout.Token);
                message = action switch
                {
                    ServiceAction.Start => "Control plane started.",
                    ServiceAction.Stop => "Control plane stopped.",
                    ServiceAction.Restart => "Control plane restarted.",
                    ServiceAction.EnterMaintenance => "Maintenance mode enabled.",
                    ServiceAction.ExitMaintenance => "Maintenance mode disabled.",
                    _ => "Service action completed."
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            accepted = false;
            message = $"{action} did not complete within {options.Value.ActionTimeoutSeconds} seconds.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Control-plane service action {Action} failed.", action);
            accepted = false;
            message = exception.Message;
        }
        finally
        {
            lock (_stateGate) _pendingAction = null;
            _actionGate.Release();
        }

        return new ServiceActionResult(accepted, message,
            await GetStatusAsync(cancellationToken));
    }

    private async Task ExecuteCoreAsync(ServiceAction action, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.ActionTimeoutSeconds);
        switch (action)
        {
            case ServiceAction.Start:
                await serviceController.StartAsync(timeout, cancellationToken);
                break;
            case ServiceAction.Stop:
                await serviceController.StopAsync(timeout, cancellationToken);
                break;
            case ServiceAction.Restart:
                await serviceController.RestartAsync(timeout, cancellationToken);
                break;
            case ServiceAction.EnterMaintenance:
                await SetMaintenanceAsync(true, cancellationToken);
                break;
            case ServiceAction.ExitMaintenance:
                await SetMaintenanceAsync(false, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task SetMaintenanceAsync(bool enabled, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("ControlPlaneApi");
        using var response = await client.PostAsJsonAsync("/api/system/maintenance",
            new { enabled }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<(ApiHealthState Health, ApiSystemStatus? Api, string? Detail)> ProbeApiAsync(
        ManagedServiceState serviceState, CancellationToken cancellationToken)
    {
        if (serviceState is ManagedServiceState.Stopped or ManagedServiceState.NotInstalled)
            return (ApiHealthState.Unreachable, null,
                serviceState == ManagedServiceState.NotInstalled
                    ? "The Windows service is not installed."
                    : "The Windows service is stopped.");

        try
        {
            var client = httpClientFactory.CreateClient("ControlPlaneApi");
            using var live = await client.GetAsync("/health/live", cancellationToken);
            if (!live.IsSuccessStatusCode)
                return (ApiHealthState.Unreachable, null, $"Liveness returned {(int)live.StatusCode}.");

            var api = await client.GetFromJsonAsync<ApiSystemStatus>("/api/system/status", cancellationToken);
            using var ready = await client.GetAsync("/health/ready", cancellationToken);
            if (api?.MaintenanceMode == true)
                return (ApiHealthState.Maintenance, api, "New commands are paused.");
            if (!ready.IsSuccessStatusCode)
                return (ApiHealthState.Degraded, api, $"Readiness returned {(int)ready.StatusCode}.");

            return (ApiHealthState.Healthy, api, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (ApiHealthState.Unreachable, null, "The API health endpoint could not be reached.");
        }
    }

    private static IReadOnlyList<ServiceAction> GetAvailableActions(ManagedServiceState state,
        ApiHealthState health, ServiceAction? pending)
    {
        if (pending is not null) return [];
        return state switch
        {
            ManagedServiceState.Stopped => [ServiceAction.Start],
            ManagedServiceState.Running when health == ApiHealthState.Maintenance =>
                [ServiceAction.ExitMaintenance, ServiceAction.Stop, ServiceAction.Restart],
            ManagedServiceState.Running =>
                [ServiceAction.EnterMaintenance, ServiceAction.Stop, ServiceAction.Restart],
            _ => []
        };
    }
}
