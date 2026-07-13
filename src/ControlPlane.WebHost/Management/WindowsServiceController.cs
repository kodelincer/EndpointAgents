using Microsoft.Extensions.Options;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace ControlPlane.WebHost.Management;

/// <summary>Controls the configured ControlPlane.Api service through Windows SCM.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController(IOptions<ManagementOptions> options)
    : IWindowsServiceController
{
    private readonly string _serviceName = options.Value.ServiceName;

    public Task<ManagedServiceState> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadStatus(), cancellationToken);

    public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var service = Create();
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        await WaitForAsync(service, ServiceControllerStatus.Running, timeout, cancellationToken);
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var service = Create();
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (!service.CanStop) throw new InvalidOperationException($"Service '{_serviceName}' cannot be stopped.");
        service.Stop();
        await WaitForAsync(service, ServiceControllerStatus.Stopped, timeout, cancellationToken);
    }

    public async Task RestartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await StopAsync(timeout, cancellationToken);
        var remaining = timeout - (DateTimeOffset.UtcNow - started);
        if (remaining <= TimeSpan.Zero) throw new System.TimeoutException("Service restart timed out while stopping.");
        await StartAsync(remaining, cancellationToken);
    }

    private ManagedServiceState ReadStatus()
    {
        try
        {
            using var service = Create();
            service.Refresh();
            return Map(service.Status);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is Win32Exception)
        {
            return ManagedServiceState.NotInstalled;
        }
    }

    private ServiceController Create() => new(_serviceName, ".");

    private static async Task WaitForAsync(ServiceController service,
        ServiceControllerStatus expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == expected) return;
            await Task.Delay(250, cancellationToken);
        }

        throw new System.TimeoutException($"Service did not reach {expected} within {timeout.TotalSeconds:0} seconds.");
    }

    private static ManagedServiceState Map(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => ManagedServiceState.Stopped,
        ServiceControllerStatus.StartPending => ManagedServiceState.StartPending,
        ServiceControllerStatus.StopPending => ManagedServiceState.StopPending,
        ServiceControllerStatus.Running => ManagedServiceState.Running,
        ServiceControllerStatus.ContinuePending => ManagedServiceState.ContinuePending,
        ServiceControllerStatus.PausePending => ManagedServiceState.PausePending,
        ServiceControllerStatus.Paused => ManagedServiceState.Paused,
        _ => ManagedServiceState.Unknown
    };
}
